<!-- Title as a Conventional Commit: type(scope): summary -->

## What and why

<!-- One paragraph. Link the issue: Fixes #123 -->

## Checklist

- [ ] Every commit is a Conventional Commit and carries a DCO `Signed-off-by` trailer (`git commit -s`)
- [ ] `dotnet build Bastion.slnx -warnaserror` and `dotnet test Bastion.slnx` pass locally
- [ ] Tests added or updated at the layer the change lives in
- [ ] `docs/FORMAT.md` / `docs/API.md` / `docs/UI-CONTRACT.md` updated if behaviour, format or API changed
- [ ] Golden fixtures still compare byte-for-byte (or the format version was bumped deliberately)
- [ ] No entry names, in-vault paths, keys or salts in logs or error messages
- [ ] UI changes verified visually (screenshots below)
- [ ] `CHANGELOG.md` entry under *Unreleased*

## Screenshots

<!-- for UI changes -->
