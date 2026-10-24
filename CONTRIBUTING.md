# Contributing

## License of contributions

OpenEleven is released under the **GNU Affero General Public License v3.0 or later**
(`AGPL-3.0-or-later`, see [LICENSE](LICENSE)). By submitting a contribution you agree
that it is licensed to the project and to everyone else under those same terms
(inbound = outbound). There is no separate CLA, so nothing here lets the maintainers
relicense your work under different terms later — if that ever matters, ask first.

## Sign-off (DCO)

Every commit must carry a `Signed-off-by` line certifying the
[Developer Certificate of Origin 1.1](https://developercertificate.org/):

```bash
git commit -s -m "fix(handlers): reject empty room name"
```

which produces:

```
Signed-off-by: Your Name <you@example.com>
```

Use your real name and an address you can be reached at. `git commit -s` adds it
automatically; `git rebase --signoff` fixes a branch that is missing it. Commits without
a sign-off are not merged. The sign-off is a statement that you wrote the patch or have
the right to submit it — it is not a copyright assignment.

## Provenance rules

This project is a reverse-engineered server. That makes provenance the one thing that
can invalidate the license:

- Do **not** paste decompiled code, disassembly, leaked sources or strings lifted from
  the game binaries. Describe the observed wire format and implement it independently.
- Do **not** copy code from GPL-2.0-only, GPL-3.0-only, or other copyleft projects
  without checking compatibility first; AGPL-3.0 is one-way compatible with GPL-3.0 but
  not with GPL-2.0-only.
- MIT/BSD/Apache-2.0 snippets are fine; record the origin in the commit message and add
  the component to [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- `main.py` and `tools/` are local reference material and are not tracked. Anything you
  derive from them must be your own expression of the protocol, not a translation.

## Code conventions

- New `.cs` files start with the SPDX header used across the tree:

  ```csharp
  // SPDX-License-Identifier: AGPL-3.0-or-later
  // Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
  //
  // This file is part of OpenEleven. See the LICENSE file at the repository root.
  ```

- Keep the wire format covered by tests: `dotnet test` must pass, and protocol changes
  need a golden vector from `tests/OpenEleven.Protocol.Tests/goldens.json` or a new one
  generated with the reference tooling.
- Match the existing style: file-scoped namespaces, nullable enabled, no regions.

## Reporting license violations

If you find a deployment that runs a modified OpenEleven without offering its source
(the AGPL section 13 obligation), contact the maintainers at
<pes5indie@gmail.com> with the address and evidence. Most operators fix it once asked;
the project's own repository is the reference source they should be pointing at.
