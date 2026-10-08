-- Reality Hack VR Museum: ratings backend.
--
--   projects  : one row per painting (synced from data/projects.json by `python -m rhm.supabase_sync`)
--   ratings   : ratings(project_id, user_id, stars), one per user per project, 1..5
--   rate_project(p_project_id, p_stars)  : upsert the caller's rating (RPC)
--   clear_rating(p_project_id)           : remove it
--   project_scores()                     : aggregates + Bayesian average for every project (RPC)
--
-- Headsets sign in with Supabase anonymous auth, so every visitor has a stable auth.uid()
-- without typing an email on a Quest.

-- ---------------------------------------------------------------- projects

create table public.projects (
    id          text primary key,
    year        smallint not null,
    title       text not null,
    platform    text,
    fidelity    text,
    exhibit     text,
    updated_at  timestamptz not null default now()
);

alter table public.projects enable row level security;

create policy "projects are public"
    on public.projects for select
    to anon, authenticated
    using (true);

-- Only the service role (the pipeline sync) writes projects.
revoke insert, update, delete, truncate on public.projects from anon, authenticated;

-- ---------------------------------------------------------------- ratings

create table public.ratings (
    project_id  text not null references public.projects (id) on delete cascade,
    user_id     uuid not null default auth.uid() references auth.users (id) on delete cascade,
    stars       smallint not null check (stars between 1 and 5),
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),
    primary key (project_id, user_id)
);

create index ratings_user_id_idx on public.ratings (user_id);

alter table public.ratings enable row level security;

-- Visitors see and change only their own rows; everyone else only sees aggregates via project_scores().
create policy "read own ratings"
    on public.ratings for select
    to authenticated
    using (user_id = (select auth.uid()));

create policy "insert own ratings"
    on public.ratings for insert
    to authenticated
    with check (user_id = (select auth.uid()));

create policy "update own ratings"
    on public.ratings for update
    to authenticated
    using (user_id = (select auth.uid()))
    with check (user_id = (select auth.uid()));

create policy "delete own ratings"
    on public.ratings for delete
    to authenticated
    using (user_id = (select auth.uid()));

revoke all on public.ratings from anon;
revoke truncate on public.ratings from authenticated;

-- ---------------------------------------------------------------- RPCs

create or replace function public.rate_project(p_project_id text, p_stars integer)
returns public.ratings
language plpgsql
security invoker
set search_path = ''
as $$
declare
    uid uuid := auth.uid();
    result public.ratings;
begin
    if uid is null then
        raise exception 'sign in (anonymous is fine) before rating' using errcode = '42501';
    end if;
    if p_stars is null or p_stars < 1 or p_stars > 5 then
        raise exception 'stars must be 1..5, got %', p_stars using errcode = '22023';
    end if;

    insert into public.ratings as r (project_id, user_id, stars)
    values (p_project_id, uid, p_stars::smallint)
    on conflict (project_id, user_id)
        do update set stars = excluded.stars, updated_at = now()
    returning r.* into result;
    return result;
end;
$$;

create or replace function public.clear_rating(p_project_id text)
returns void
language sql
security invoker
set search_path = ''
as $$
    delete from public.ratings where project_id = p_project_id and user_id = auth.uid();
$$;

-- Bayesian average: (C * m + sum(stars)) / (C + n)
--   m = mean of all ratings in the museum (3.0 before any exist)
--   C = prior weight in "virtual ratings". With C = 5, one 5-star vote can't beat a project
--       rated 4.6 by twenty people.
-- SECURITY DEFINER so it can aggregate across users while ratings rows stay private; it
-- exposes only counts and averages.
create or replace function public.project_scores(prior_weight numeric default 5)
returns table (
    project_id    text,
    rating_count  integer,
    avg_stars     numeric,
    bayes_score   numeric
)
language sql
stable
security definer
set search_path = ''
as $$
    with g as (
        select coalesce(avg(stars), 3.0) as m from public.ratings
    ),
    agg as (
        select r.project_id, count(*)::integer as n, sum(r.stars)::numeric as s
        from public.ratings r
        group by r.project_id
    )
    select p.id,
           coalesce(agg.n, 0),
           case when agg.n > 0 then round(agg.s / agg.n, 3) end,
           round((greatest(prior_weight, 0) * g.m + coalesce(agg.s, 0))
                 / nullif(greatest(prior_weight, 0) + coalesce(agg.n, 0), 0), 4)
    from public.projects p
    cross join g
    left join agg on agg.project_id = p.id
    order by 4 desc nulls last, 2 desc, p.id;
$$;

revoke execute on function public.rate_project(text, integer) from public, anon;
revoke execute on function public.clear_rating(text) from public, anon;
revoke execute on function public.project_scores(numeric) from public;
grant execute on function public.rate_project(text, integer) to authenticated;
grant execute on function public.clear_rating(text) to authenticated;
grant execute on function public.project_scores(numeric) to anon, authenticated;
