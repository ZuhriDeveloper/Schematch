# Schematch

A Windows desktop (WinForms) tool to compare two databases — schema **and** data — and
generate the SQL script that makes a **target** database match a **source**, in the style of
Redgate SQL Compare / SQL Data Compare.

Supports **SQL Server** and **PostgreSQL** (same-engine pairs).

## Features

- **Schema compare** across tables (columns, primary/foreign keys, unique & check constraints,
  indexes, identity/computed columns, defaults) plus views, stored procedures, functions and triggers.
- **Side-by-side DDL diff** with line-level highlighting.
- **Deployment script generation** in dependency-safe phases (drop code → drop FKs → drop
  constraints/indexes → drop tables → create tables → alter columns → recreate constraints →
  indexes → FKs → create/replace code objects), with `-- WARNING:` comments on destructive or
  lossy operations and a transaction wrapper.
- **Execute against target** with a typed-database-name confirmation and an execution log.
- **Data compare** via a streaming merge-join (large tables never load fully into memory),
  generating `INSERT`/`UPDATE`/`DELETE` scripts.
- Each connection can be entered as **structured fields** (engine, host, auth, database) **or as a
  raw connection string** — tick "Enter a connection string directly" in the connection dialog.
  The engine picker still selects the driver; the database is parsed out of the string.
- **PostgreSQL** connections can be **scoped to a single schema** (pick it in the connection dialog's
  Schema box). Leave it as "(all schemas)" to compare the whole database.
- Recent connections saved to `%APPDATA%\Schematch\settings.json`; passwords (and saved connection
  strings, which may embed a password) are optional and, when saved, encrypted per Windows user with DPAPI.

## Projects

| Project | Purpose |
|---|---|
| `src/Schematch.Core` | Engine-neutral schema model, provider abstraction, diff engine, script generators, data compare. `net10.0`. |
| `src/Schematch.App` | WinForms UI. `net10.0-windows`. |
| `tests/Schematch.Tests` | xUnit tests for the diff engine, script generators, and literal formatters. |
| `tools/Schematch.E2E` | Console round-trip verifier against real databases. |

## Build & test

```sh
dotnet build
dotnet test
```

## Run the app

```sh
dotnet run --project src/Schematch.App
```

Then set a Source and Target connection, click **Compare**, review the differences, and use
**Generate Deployment Script** or **Data Compare…**.

## End-to-end verification

The E2E tool provisions two demo databases with deliberate drift, then proves the round-trip:
compare → generate script → execute → re-compare shows **zero** differences (schema and data).

```sh
# SQL Server (uses (localdb)\MSSQLLocalDB)
dotnet run --project tools/Schematch.E2E -- mssql

# PostgreSQL (uses localhost:5432; provide the postgres password)
dotnet run --project tools/Schematch.E2E -- pg --pg-password=YOURPASSWORD
# or set SCHEMATCH_PG_PASSWORD in the environment
```

`--provision-only` sets up the drifted demo databases without running the sync — handy for
exercising the UI (`Schematch.App --demo-mssql` then loads them and compares automatically).

## Releases

Every push to `master` triggers [`.github/workflows/release.yml`](.github/workflows/release.yml),
which runs the test suite, publishes `Schematch.App` as a self-contained win-x64 single-file
executable, and attaches it (zipped and as a raw `.exe`) to a new GitHub Release.

Each release is tagged `v<version>.<run-number>` — `<version>` comes from `<Version>` in
[`Schematch.App.csproj`](src/Schematch.App/Schematch.App.csproj) (bump it there for a new
version line) and `<run-number>` is the Actions run number, keeping every tag unique. The
workflow can also be started manually from the Actions tab (**Run workflow**).
