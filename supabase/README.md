# Ratings backend (phase 3: Supabase)

`ratings(project_id, user_id, stars)`, sorted by Bayesian average. Each headset is one anonymous voter, so nobody types an email on a Quest.

## Schema (`migrations/20261004000000_ratings.sql`)

| Object | Access | Purpose |
|---|---|---|
| `projects(id, year, title, platform, fidelity, exhibit)` | read: everyone · write: service role only | One row per painting; the foreign key target for ratings |
| `ratings(project_id, user_id, stars 1–5)` | RLS: a user reads and changes only their own rows · anon: none | PK `(project_id, user_id)`, so one vote per visitor per project; changing your mind updates the row |
| `rate_project(p_project_id, p_stars)` | authenticated | Upsert the caller's rating; validates 1–5 and that the project exists |
| `clear_rating(p_project_id)` | authenticated | Remove your rating |
| `project_scores(prior_weight = 5)` | anon + authenticated | Per project: `rating_count`, `avg_stars`, `bayes_score`. Aggregates only; raw votes stay private. |

**Bayesian score:** `(C·m + Σstars) / (C + n)`.
- `m` is the mean of all ratings in the museum (3.0 before any exist).
- `C = 5` is the number of "virtual ratings" at the mean.
- Effect: one 5-star vote (score 3.33 when m = 3) can't outrank a project rated 4.6 by twenty visitors (4.28).

## Set up (about 10 minutes)

**On supabase.com (dashboard):**
1. **New project.** Any name and region; save the database password somewhere.
2. **SQL Editor → New query:** paste all of `migrations/20261004000000_ratings.sql` and **Run**.
3. **Authentication → Sign In / Providers → Allow anonymous sign-ins: on.** Save.
4. **Project Settings → API Keys:** copy the **Publishable key** (`sb_publishable_…`) and the **Secret key** (`sb_secret_…`). The legacy `anon` / `service_role` keys work too. Also copy the project URL (`https://<ref>.supabase.co`).

**On the PC (repo root):**
```bash
pip install -r pipeline/requirements.txt
python supabase/setup.py --url https://<ref>.supabase.co --key sb_publishable_... --service-key sb_secret_...
git add data/backend.json data/museum.json && git commit -m "Ratings backend" && git push
```

`setup.py` does the following:
1. Checks the migration.
2. Loads the 444 projects using the secret key. The key is used only for this call and is never written anywhere.
3. Writes `data/backend.json` with the URL and the publishable key.
4. Rebuilds `data/museum.json` with a `"ratings"` block.
5. Runs `smoke.py`.

Installed museums turn ratings on the next time they fetch `museum.json` from `main`, with no rebuild. Inspector values on **Museum Bootstrap** (`supabaseUrl`, `supabaseAnonKey`) still override, if you want a test backend.

**Alternative: Supabase CLI.** `config.toml` is committed with anonymous sign-ins on:
1. `supabase link --project-ref <ref>`
2. `supabase db push`
3. `supabase config push`
4. Then run `setup.py` as above.

After each pipeline rebuild, rerun `python -m rhm.supabase_sync push` (with `SUPABASE_URL` / `SUPABASE_SERVICE_ROLE_KEY`) to add new projects. Scores order paintings automatically, because `layout.py` reads `data/backend.json`.

## Live check (`smoke.py`)

```bash
python supabase/smoke.py --url https://<ref>.supabase.co --key sb_publishable_...
```

It makes the same HTTP calls as `RatingsClient.cs`, with the same headers:
- anonymous sign-up and token refresh
- `project_scores`
- `rate_project` twice (the second vote must replace the first)
- reading your own votes
- RLS (the key alone can read no votes and can't rate)
- `clear_rating`, which leaves no trace

It refuses a secret key.

**Verified 2026-10-09** against a real local Supabase stack (CLI 2.54, Postgres 17, GoTrue, PostgREST, Kong) with this migration and `config.toml`:
- all checks pass with both the publishable key and the legacy anon JWT
- `setup.py` ran end to end, including the "migration not applied" path
- `supabase_sync push` works with the new `sb_secret_` key

To run the stack locally:

```bash
supabase start -x realtime,storage-api,imgproxy,studio,edge-runtime,logflare,vector,supavisor,postgres-meta,mailpit
```

Realtime is disabled in `config.toml` because the museum doesn't use it.

## Tests

```bash
supabase/tests/run.sh   # needs postgresql (initdb, pg_ctl, psql); prints ALL RATINGS TESTS PASSED
```

The script spins up a throwaway Postgres cluster and loads `tests/supabase_stub.sql`, a stand-in for Supabase's `auth` schema, `auth.uid()`, and the anon/authenticated/service_role roles with the default grants. It then applies the migration and checks:
- anon can't rate or read votes, but can read projects and scores
- upsert replaces a vote rather than adding a second one
- out-of-range stars and unknown project IDs are rejected
- a user can't insert a rating as someone else, or update or delete another user's rating
- nobody but the service role can write `projects`
- deleting a user cascades to their ratings
- Bayesian values and ordering match hand-computed numbers

A negative control was run: loosening the read policy fails the suite (`FAIL B sees others`).

## Abuse notes

- Anonymous sign-ins are rate-limited by Supabase (default 30/hour per IP). Someone could still farm votes by wiping app data repeatedly. For a public museum, the next step would be one of:
  - Meta platform entitlement checks (one voter per Quest account), or
  - Supabase CAPTCHA on sign-up (awkward in VR).
- The headset keeps its refresh token in `PlayerPrefs`. Uninstalling the app makes it a new voter.
