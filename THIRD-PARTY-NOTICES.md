# Third-party notices

OpenEleven is distributed under `AGPL-3.0-or-later` (see [LICENSE](LICENSE)). It links
the following third-party components. All of them are permissively licensed and
compatible with the AGPL; none of them change the license of this project.

| Component | License | Notes |
|---|---|---|
| .NET 9 runtime (Microsoft) | MIT | Platform/runtime, not redistributed by this repository |
| Microsoft.EntityFrameworkCore | MIT | ORM and SQLite provider |
| Microsoft.Extensions.Logging.Abstractions | MIT | Logging abstractions |
| Pomelo.EntityFrameworkCore.MySql | MIT | MySQL provider for EF Core |
| BouncyCastle.Cryptography | MIT | Blowfish-ECB, hashing primitives |
| NetEscapades.Configuration.Yaml | MIT | YAML configuration provider |

Package versions are pinned in the individual `.csproj` files; the shared EF Core and
Extensions versions live in [Directory.Build.props](Directory.Build.props).

Text/MIT license copies:

- MIT — <https://opensource.org/license/mit>
- AGPL-3.0 — <https://www.gnu.org/licenses/agpl-3.0.html>

The game client, the PES titles, their artwork, audio, team and player data, and the
network protocol implemented here are **not** covered by any of the above and are not
licensed by this project. Running this server requires game files you already own.
