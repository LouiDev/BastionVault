# Golden fixtures

The two `.bastion` files in this folder are the golden vaults of `docs/FORMAT.md` section 10. They are
checked in as binaries and every test run rebuilds them from scratch and compares the result **byte for
byte**. A difference means one of two things:

1. the on-disk format changed (deliberately, and `docs/FORMAT.md` says so), or
2. a writer that is supposed to be deterministic no longer is — a real bug.

Never regenerate a fixture to make a red test green. Find out which of the two it is first.

## What is in them

Both are produced by `Bastion.Core.Tests.Vault.GoldenVault` with every source of variation pinned:

| Knob        | Value                                                        |
|-------------|--------------------------------------------------------------|
| randomness  | `DeterministicRandomSource(seed: 0)`                          |
| clock       | `FixedClock(2026-01-01T00:00:00Z)`                            |
| KDF         | `KdfParameters(8192, 1, 1)` — the cheapest the limits table allows |
| password    | `correct horse battery staple`                                |
| keyfile     | none                                                          |
| timestamps  | `ImportOptions(PreserveTimestamps: false)`, so the file system of the build machine cannot leak in |

`golden-v1-empty.bastion` — a vault created and closed without a single edit. It is the smallest legal
vault: 160 bytes of header plus two 65 552-byte index copies, `saveCounter = 1`, no data section.

`golden-v1-small.bastion` — created, then filled in exactly this order and saved once:

| Entry                       | Content                                                        |
|-----------------------------|----------------------------------------------------------------|
| `\Documents`                | folder                                                          |
| `\Documents\2026`           | folder                                                          |
| `\Documents\2026\a.txt`     | `abc`, plus a non-ASCII comment                                 |
| `\empty.bin`                | zero bytes — the one-empty-chunk case of section 2.7            |
| `\big.bin`                  | 2 MiB + 17 deterministic bytes — three chunks, the last one short |

The order matters: entry ids are handed out in creation order and the index is serialized depth-first
pre-order, so a different order is a different file.

## Regenerating

Set the environment variable `BASTION_REGEN_GOLDEN=1` and run the test project once. The two fixture
tests then overwrite the files instead of comparing them:

```powershell
$env:BASTION_REGEN_GOLDEN = "1"
dotnet test tests/Bastion.Core.Tests --filter "FullyQualifiedName~GoldenFixtureTests"
Remove-Item Env:\BASTION_REGEN_GOLDEN
```

```bash
BASTION_REGEN_GOLDEN=1 dotnet test tests/Bastion.Core.Tests --filter "FullyQualifiedName~GoldenFixtureTests"
```

Run the whole suite again afterwards **without** the variable: the fixtures are also opened, verified,
exported and taken apart byte by byte by the tamper matrix, so a bad regeneration is caught immediately.
Commit the changed binaries together with the format change that caused them.

## Who reads them

* `Vault/GoldenFixtureTests` — rebuild and compare, open and check the tree, contents, `Statistics`,
  the blob layout and the padding ladder, export and compare.
* `Vault/GoldenVault` — the recipe itself, shared with the tamper tests, which use the same seams so
  that a damaged vault differs from a healthy one in exactly the byte they damaged.
