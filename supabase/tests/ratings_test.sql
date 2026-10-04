-- Run after supabase_stub.sql + migrations. Any failed assertion raises and aborts (ON_ERROR_STOP).
\set ON_ERROR_STOP on
set client_min_messages = warning;

-- fixtures (as superuser = service role equivalent)
insert into auth.users values ('00000000-0000-0000-0000-00000000000a'), ('00000000-0000-0000-0000-00000000000b'),
                              ('00000000-0000-0000-0000-00000000000c');
insert into public.projects (id, year, title) values ('2024-a', 2024, 'A'), ('2024-b', 2024, 'B'), ('2024-c', 2024, 'C');

create or replace function pg_temp.as_user(u text) returns void language sql as $$
  select set_config('request.jwt.claim.sub', u, false);
$$;

-- 1. anon cannot rate, cannot read ratings, can read projects + scores
set role anon;
select pg_temp.as_user('');
do $$ begin
  begin perform public.rate_project('2024-a', 5); raise exception 'FAIL anon rated';
  exception when insufficient_privilege then null; end;
  begin perform count(*) from public.ratings; raise exception 'FAIL anon read ratings';
  exception when insufficient_privilege then null; end;
  if (select count(*) from public.projects) <> 3 then raise exception 'FAIL anon projects'; end if;
  if (select count(*) from public.project_scores()) <> 3 then raise exception 'FAIL anon scores'; end if;
  begin insert into public.projects values ('x', 2024, 'x'); raise exception 'FAIL anon wrote projects';
  exception when insufficient_privilege then null; end;
end $$;
reset role;

-- 2. user A rates; upsert replaces; bounds enforced
set role authenticated;
select pg_temp.as_user('00000000-0000-0000-0000-00000000000a');
do $$ begin
  perform public.rate_project('2024-a', 4);
  perform public.rate_project('2024-a', 5);          -- change of mind = update, not a second row
  perform public.rate_project('2024-b', 2);
  if (select count(*) from public.ratings) <> 2 then raise exception 'FAIL A row count'; end if;
  if (select stars from public.ratings where project_id = '2024-a') <> 5 then raise exception 'FAIL upsert'; end if;
  begin perform public.rate_project('2024-a', 6); raise exception 'FAIL stars=6 accepted';
  exception when invalid_parameter_value then null; end;
  begin perform public.rate_project('2024-a', 0); raise exception 'FAIL stars=0 accepted';
  exception when invalid_parameter_value then null; end;
  begin perform public.rate_project('nope', 3); raise exception 'FAIL unknown project accepted';
  exception when foreign_key_violation then null; end;
  -- direct insert pretending to be someone else
  begin insert into public.ratings (project_id, user_id, stars) values ('2024-c', '00000000-0000-0000-0000-00000000000b', 5);
        raise exception 'FAIL impersonation insert';
  exception when insufficient_privilege then null; end;
  begin insert into public.projects values ('y', 2024, 'y'); raise exception 'FAIL authenticated wrote projects';
  exception when insufficient_privilege then null; end;
end $$;

-- 3. user B: sees only own rows, cannot modify A's
select pg_temp.as_user('00000000-0000-0000-0000-00000000000b');
do $$
declare n int;
begin
  perform public.rate_project('2024-a', 3);
  if (select count(*) from public.ratings) <> 1 then raise exception 'FAIL B sees others'; end if;
  update public.ratings set stars = 1 where user_id = '00000000-0000-0000-0000-00000000000a';
  get diagnostics n = row_count;
  if n <> 0 then raise exception 'FAIL B updated A'; end if;
  delete from public.ratings where user_id = '00000000-0000-0000-0000-00000000000a';
  get diagnostics n = row_count;
  if n <> 0 then raise exception 'FAIL B deleted A'; end if;
end $$;

-- 4. user C rates a 5 on c; clear_rating removes only own
select pg_temp.as_user('00000000-0000-0000-0000-00000000000c');
do $$ begin
  perform public.rate_project('2024-c', 5);
  perform public.rate_project('2024-b', 4);
  perform public.clear_rating('2024-b');
  if (select count(*) from public.ratings) <> 1 then raise exception 'FAIL clear_rating'; end if;
end $$;
reset role;

-- 5. Bayesian scores. Ratings now: a = {5 (A), 3 (B)}, b = {2 (A)}, c = {5 (C)}.
--    m = (5+3+2+5)/4 = 3.75, C = 5
--    a: (5*3.75 + 8)/7 = 3.8214   b: (18.75 + 2)/6 = 3.4583   c: (18.75 + 5)/6 = 3.9583
do $$
declare r record;
begin
  for r in select * from public.project_scores() loop
    if r.project_id = '2024-a' and (r.rating_count <> 2 or r.avg_stars <> 4.000 or r.bayes_score <> 3.8214) then raise exception 'FAIL a %', r; end if;
    if r.project_id = '2024-b' and (r.rating_count <> 1 or r.bayes_score <> 3.4583) then raise exception 'FAIL b %', r; end if;
    if r.project_id = '2024-c' and (r.rating_count <> 1 or r.bayes_score <> 3.9583) then raise exception 'FAIL c %', r; end if;
  end loop;
  if (select string_agg(project_id, ',') from public.project_scores()) <> '2024-c,2024-a,2024-b' then
    raise exception 'FAIL order %', (select string_agg(project_id, ',') from public.project_scores());
  end if;
  -- prior_weight 0 = plain average ranking: a (4.0) beats c? no: c is 5.0
  if (select bayes_score from public.project_scores(0) where project_id = '2024-c') <> 5 then raise exception 'FAIL C=0'; end if;
end $$;

-- 6. deleting a user cascades their ratings
delete from auth.users where id = '00000000-0000-0000-0000-00000000000c';
do $$ begin
  if exists (select 1 from public.ratings where project_id = '2024-c') then raise exception 'FAIL cascade'; end if;
end $$;

select 'ALL RATINGS TESTS PASSED' as result;
