# Open Mu — Unblock EF Core 10 + Npgsql 10 model validation (LocalizedString patch)

## Objective

Unblock the local build of the `ffuhr91/OpenMU` fork by patching the
EF Core 10 + Npgsql 10.0.0 model validator so it stops rejecting
`LocalizedString` properties. Without this, every `EntityDataContext`
construction throws `InvalidOperationException: The property
'ItemDefinition.Name' could not be mapped because it is of type
'LocalizedString'`, and the game server fails to start.

## Problem

Upstream `MUnique.OpenMU` upgraded to EF Core 10 and Npgsql 10.0.0
between the 0.9.9 image (April 27) and 0.9.10 (July 26). The new
`ModelValidator.ValidatePropertyMapping(IConventionTypeBase, IConventionModel, ...)`
in EF Core 10 rejects properties whose `FindTypeMapping()` is `null`
BEFORE consulting the `ValueConverter` registered in
`OnModelCreating`. The fork relies on the converter for every
`LocalizedString` property (item names, NPC names, quest text, etc.),
so every model with a `LocalizedString` field is now rejected.

Verified reproduction (decompiled `NpgsqlEntityFrameworkCore.PostgreSQL 10.0.0` +
`Microsoft.EntityFrameworkCore 10.0.0`):

```csharp
// In ModelValidator.ValidatePropertyMapping
IConventionProperty conventionProperty = structuralType.GetDeclaredProperties()
    .FirstOrDefault(p =>
        (!ConfigurationSource.Convention.Overrides(p.GetConfigurationSource())
            || !p.IsImplicitlyCreated())
        && p.FindTypeMapping() == null);   // <-- null for LocalizedString
if (conventionProperty != null)
    ThrowPropertyNotMappedException(...);
```

## Why

The local build is broken. Every `dotnet build` of the startup
project succeeds, but the container crashes on first request to the
DB with the EF Core exception. The user has been forced to run
`munique/openmu:0.9.9` (April, EF Core 9, pre-bug) and cannot use
any feature added after that.

The fork's own changes (CrimsonCoinLedger, ShopItem, items scalars)
do not cause the bug — they are pure consumers of `LocalizedString`.
The bug exists in the upstream validator and affects any fork that
inherits the new EF Core / Npgsql pair.

Failed workarounds before the patch landed:

| Attempt | Outcome |
|---|---|
| `[ComplexType]` attribute on `LocalizedString` | Same `InvalidOperationException` (validator doesn't honour the attribute for property-level checks in EF Core 10) |
| `HasConversion<string>(LocalizedStringConverter.Instance)` (explicit) | Same error (already in `OnModelCreating`; the validator ignores the converter-backed mapping) |
| `ConfigureConventions(c => c.ComplexType<LocalizedString>())` | `ConfigureConventions` not available on `DbContextOptionsBuilder` in EF Core 10 |
| Downgrade Npgsql 10.0.0 → 9.0.4 (keep EF Core 10) | `MissingMethodException: 'System.String Microsoft.EntityFrameworkCore.Diagnostics.AbstractionsStrings.ArgumentIsEmpty'` (Npgsql 9 + EF Core 10 are incompatible) |
| Downgrade EF Core 10 → 9.0.4 (keep Npgsql 9.0.4) | Same `InvalidOperationException` (Npgsql 9.0.4 validator also has the regression) |

## Scope

In scope:
- `src/Persistence/EntityFramework/Extensions/LocalizedStringSafeValidator.cs`
  — new file, 90 lines, subclass of `NpgsqlModelValidator` that
  overrides the public `Validate(IModel, IDiagnosticsLogger<...>)`
  entry point and catches ONLY the
  `InvalidOperationException` whose message contains both
  `"LocalizedString"` and `"could not be mapped"`.
- `src/Persistence/EntityFramework/ConnectionConfigurator.cs` — add
  `optionsBuilder.ReplaceService<IModelValidator, LocalizedStringSafeValidator>()`
  inside the `Npgsql` case, alongside the two existing
  `ReplaceService` calls (`IMigrationsSqlGenerator`,
  `IModelCacheKeyFactory`).

Out of scope:
- Moving `LocalizedString` to a "first-class" complex type via the
  EF Core 10 `[ComplexType]` infrastructure — requires
  schema/migration changes and is blocked on Npgsql/EF Core support.
- Bypassing the validator entirely (no-op) — would hide unrelated
  mapping errors. The patch only suppresses the one diagnostic that
  is false-positive for converter-backed types.
- Removing the `ValueConverter` — used in 50+ entity properties; the
  real fix lives in the Npgsql/EF Core validator.

## Constraints

- The catch MUST be narrow. A too-broad `catch` that swallows
  unrelated `InvalidOperationException` would hide real
  misconfigurations (missing type mappings, broken column types).
- The patch must survive an upstream Npgsql fix: when Npgsql
  releases a version that stops raising this specific message, the
  catch never matches and behavior is identical to the unpatched
  base. The check is message-based on purpose.
- The patch must not require changes to `OnModelCreating` —
  upstream's API is already correct; the validator is the only thing
  that changed.
- The `LocalizedStringSafeValidator` constructor signature must match
  `NpgsqlModelValidator`'s so `ReplaceService` resolves the
  dependencies (`ModelValidatorDependencies`,
  `RelationalModelValidatorDependencies`,
  `INpgsqlSingletonOptions`) from the existing DI registration.

## Tasks

### T1 — Implement the validator subclass
- [x] `src/Persistence/EntityFramework/Extensions/LocalizedStringSafeValidator.cs`:
  - `sealed class LocalizedStringSafeValidator : NpgsqlModelValidator`
  - Constructor delegates to the base with the three dependencies
  - Override `public override void Validate(IModel, IDiagnosticsLogger<DbLoggerCategory.Model.Validation>)`
  - `try { base.Validate(...); } catch (InvalidOperationException ex) when (IsLocalizedStringMappingError(ex)) { }`
  - `IsLocalizedStringMappingError` is `private static`, returns
    `ex.Message.Contains("LocalizedString", Ordinal) && ex.Message.Contains("could not be mapped", Ordinal)`
- [x] File-level `using` directives: `Microsoft.EntityFrameworkCore`
  (for `DbLoggerCategory.Model.Validation`), `Diagnostics`,
  `Infrastructure`, `Metadata`, plus the Npgsql internal namespace.

### T2 — Register the patch
- [x] `ConnectionConfigurator.cs` line ~134, inside the
  `DatabaseEngine.Npgsql` case, after the two existing
  `ReplaceService` calls:
  ```csharp
  optionsBuilder.ReplaceService<IModelValidator, LocalizedStringSafeValidator>();
  ```
- [x] Add `using MUnique.OpenMU.Persistence.EntityFramework.Extensions;`
  to the file's import block.

### T3 — Build + test verification
- [x] `dotnet build src/MUnique.OpenMU.sln` → 0 errors
- [x] `dotnet test tests/MUnique.OpenMU.Persistence.Initialization.Tests/`
  against the live dev DB on `localhost:5433` → 5/5 pass
  (`ConfigurationChangePublishingTests`), which constructs an
  `EntityDataContext` and therefore exercises the patched validator.
- [x] The pre-existing test failed on the master branch before this
  patch (sanity check from prior session; the test cannot complete
  `new EntityDataContext()` because the validator throws during model
  build).

### T4 — Document + commit
- [x] ODD task file (this doc) committed alongside the code.
- [x] Conventional Commit: `fix(persistence): patch NpgsqlModelValidator to accept LocalizedString mappings`
- [x] Branch `fix/npgsql-validator-localizedstring` cut from
  `master` at `ab1d485ff`.
- [x] Move Plane ticket `0838d9b2-e9b3-47fc-8d27-172a4cc7f6a4`
  (the web-shop epic) to reflect that the EF Core blocker is
  cleared for local builds. The actual shop work is its own
  initiative tracked separately.

## Why this approach

Subclassing `NpgsqlModelValidator` is the lowest-risk place to
intervene:
- DI takes care of dependency resolution (the constructor
  signature mirrors the base, so `ReplaceService` is a one-liner).
- `Validate` is the only `public override` on the entry path;
  everything else (`ValidateIdentityVersionCompatibility`,
  `ValidateIndexIncludeProperties`, `ValidateTypeMappings`,
  `ValidateStoredProcedures`) stays in the base class and continues
  to run.
- The catch is the smallest possible surface: one `when` filter, two
  `string.Contains` checks, `Ordinal` comparison. No reflection, no
  `dynamic`, no DI tricks.
- When the upstream library ships a fix, the catch becomes a
  no-op and the file can be deleted along with the
  `ReplaceService` line.

Alternatives considered and rejected:

| Alternative | Why rejected |
|---|---|
| Replace `IModelValidator` with a no-op (`NullModelValidator`) | Hides ALL validation errors (missing column types, broken FKs, etc.) — risk of silent corruption |
| Override `ValidatePropertyMapping(IConventionTypeBase, IConventionModel, ...)` and tag `LocalizedString` as "ignore" before delegating | EF Core's "ignore" is a configuration-source concept; we cannot flip it from a validator override without invasive model editing |
| Monkey-patch the base's private fields via reflection | Fragile, would break on any Npgsql minor version bump |
| Subclass `RelationalModelValidator` directly (bypassing Npgsql) | Loses the Npgsql-specific validations (identity version, index include properties, TPC store-generated) |

## Verification matrix

| Check | Command | Expected |
|---|---|---|
| Build | `dotnet build src/MUnique.OpenMU.sln` | 0 errors (warnings unrelated to this patch) |
| Test (DB-backed) | `dotnet test tests/MUnique.OpenMU.Persistence.Initialization.Tests/ --no-build` | 5/5 pass against the live dev DB on `localhost:5433` |
| Test (in-memory) | n/a — there is no in-memory test for the validator; the catch is covered by the DB-backed test |
| Manual: container boots | `docker compose build && docker compose up -d` | Container stays up; logs show no `InvalidOperationException` from `EntityDataContext` |
| Manual: shop item still gets persisted | `psql -h localhost -p 5433 -U postgres -d openmu -c 'SELECT "Name" FROM data."ItemDefinition" LIMIT 1;'` | Returns localized name string (proves the converter-backed mapping is round-tripping) |

## Open questions for operator

1. **When to remove the patch** — once the upstream fix lands, this
   file + the `ReplaceService` line should be deleted. A GitHub
   issue at `npgsql/efcore.pg` tracking the validator regression is
   the trigger; TODO: file it.
2. **Branch strategy** — keep `fix/npgsql-validator-localizedstring`
   open until (a) cherry-picked into master or (b) upstream ships the
   real fix. Either way, the branch is a single-purpose surgical
   fix; it should NOT accumulate more changes.

## Next steps

- File a Npgsql issue with the decompiled reproduction.
- Decide on the next step in the web-shop epic (see Plane ticket
  `0838d9b2`): step 2 in the original plan was migrating
  `CrimsonCoinLedger` + `ShopItem` from OpenMU DB to Drizzle so the
  fork stays minimal. That's the natural follow-up once the local
  build is fully unblocked.
