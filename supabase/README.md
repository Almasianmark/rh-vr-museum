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

1. Create a project at supabase.com.
2. Authentication → Sign In / Providers → **Allow anonymous sign-ins: on**.
3. Apply the migration, using either:
   - `supabase link --project-ref <ref> && supabase db push` (Supabase CLI, run from the repo root), or
   - pasting the SQL file into the SQL Editor.
4. Load the projects:
   ```bash
   cd pipeline
   export SUPABASE_URL=https://<ref>.supabase.co
   export SUPABASE_SERVICE_ROLE_KEY=...   # Project Settings → API. Server-side only, never in the client.
   python -m rhm.supabase_sync push      # upserts all 444 projects; rerun after each pipeline build
   ```
5. In Unity, on the **Museum Bootstrap** object, set `supabaseUrl` and `supabaseAnonKey`. The anon key is public by design; RLS and the RPC grants are what protect the data.
6. Optional: order paintings by score inside each device group:
   ```bash
   SUPABASE_URL=... SUPABASE_ANON_KEY=... python -m rhm.layout   # rewrites data/museum.json
   python -m rhm.supabase_sync scores                             # top-25 leaderboard in the terminal
   ```

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
