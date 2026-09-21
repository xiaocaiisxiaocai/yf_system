# Third-Party Notices

This distribution includes the following .NET runtime dependencies resolved by
`Yf.Api/packages.lock.json` for `net10.0` and `net10.0/win-x64`. The package type
below is the Direct or Transitive value recorded in that lock file. Complete
license and notice texts are shipped in the adjacent `licenses` directory.

The release also bundles ClamAV 1.4.6 LTS as an independent Windows x64
service process. Yf.Api communicates with it through the loopback ClamD TCP
protocol and does not link to `libclamav`.

| Package | Version | Type | License and notice files |
| --- | --- | --- | --- |
| Konscious.Security.Cryptography.Argon2 | 1.3.1 | Direct | `licenses/Konscious.Security.Cryptography-LICENSE.txt` |
| MailKit | 4.17.0 | Direct | `licenses/MailKit-LICENSE.txt` |
| Microsoft.EntityFrameworkCore | 9.0.0 | Direct | `licenses/dotnet-LICENSE.txt`, `licenses/dotnet-THIRD-PARTY-NOTICES.txt` |
| Microsoft.EntityFrameworkCore.Design | 9.0.0 | Direct | `licenses/dotnet-LICENSE.txt`, `licenses/dotnet-THIRD-PARTY-NOTICES.txt` |
| MySqlConnector | 2.6.2 | Direct | `licenses/MySqlConnector-LICENSE.txt` |
| Pomelo.EntityFrameworkCore.MySql | 9.0.0 | Direct | `licenses/Pomelo.EntityFrameworkCore.MySql-LICENSE.txt` |
| SharpCompress | 0.50.4 | Direct | `licenses/SharpCompress-LICENSE.txt` |
| System.IdentityModel.Tokens.Jwt | 8.22.0 | Direct | `licenses/IdentityModel-LICENSE.txt` |
| BouncyCastle.Cryptography | 2.6.2 | Transitive | `licenses/BouncyCastle-LICENSE.md` |
| Konscious.Security.Cryptography.Blake2 | 1.1.1 | Transitive | `licenses/Konscious.Security.Cryptography-LICENSE.txt` |
| Microsoft.Bcl.Cryptography | 10.0.2 | Transitive | `licenses/dotnet-LICENSE.txt`, `licenses/dotnet-THIRD-PARTY-NOTICES.txt` |
| Microsoft.IdentityModel.Abstractions | 8.22.0 | Transitive | `licenses/IdentityModel-LICENSE.txt` |
| Microsoft.IdentityModel.JsonWebTokens | 8.22.0 | Transitive | `licenses/IdentityModel-LICENSE.txt` |
| Microsoft.IdentityModel.Logging | 8.22.0 | Transitive | `licenses/IdentityModel-LICENSE.txt` |
| Microsoft.IdentityModel.Tokens | 8.22.0 | Transitive | `licenses/IdentityModel-LICENSE.txt` |
| MimeKit | 4.17.0 | Transitive | `licenses/MimeKit-LICENSE.txt` |
| System.Security.Cryptography.Pkcs | 10.0.0 | Transitive | `licenses/dotnet-LICENSE.txt`, `licenses/dotnet-THIRD-PARTY-NOTICES.txt` |
| ClamAV portable distribution | 1.4.6 LTS | Separate program | `licenses/ClamAV-NOTICE.md`; upstream `COPYING.txt` and dependency notices remain inside `clamav/distribution/clamav-1.4.6.win.x64.zip` |

## Provenance

- SharpCompress 0.50.4 declares MIT in its NuGet metadata. Its license is from
  package repository commit `c083c6efd843a844b0c8f7878787360e815be781`:
  `https://github.com/adamhathcock/sharpcompress/blob/c083c6efd843a844b0c8f7878787360e815be781/LICENSE.txt`.
- Microsoft.EntityFrameworkCore and Microsoft.EntityFrameworkCore.Design 9.0.0 declare MIT
  in their NuGet metadata; their package repository commit is
  `645f3131a5b0a4bf677201cf22773990a5316c89`.
- Pomelo.EntityFrameworkCore.MySql 9.0.0 declares MIT in its NuGet metadata.
  Its bundled license is from package repository commit
  `58dd6883cb9616fe49f671b943a826b459c0117f`.
- Konscious.Security.Cryptography.Argon2 1.3.1 and Blake2 1.1.1 declare MIT.
  Their shared license is from package repository commit
  `4ed95a5377e411506ca6868409b5c7d7ecaa859b` at
  `https://github.com/kmaragon/Konscious.Security.Cryptography/blob/4ed95a5377e411506ca6868409b5c7d7ecaa859b/LICENSE`.
- MailKit 4.17.0 and MimeKit 4.17.0 declare MIT. Their complete texts are from
  the matching `4.17.0` repository tags:
  `https://github.com/jstedfast/MailKit/blob/4.17.0/LICENSE` and
  `https://github.com/jstedfast/MimeKit/blob/4.17.0/LICENSE`.
- MySqlConnector 2.6.2 declares MIT. Its complete license is from package
  repository commit `775689bb3e1ab55d16171e0795264fa746e364c4` at
  `https://github.com/mysql-net/MySqlConnector/blob/775689bb3e1ab55d16171e0795264fa746e364c4/LICENSE`.
- System.IdentityModel.Tokens.Jwt and its four Microsoft.IdentityModel
  dependencies at 8.22.0 declare MIT. Their shared license is from package
  repository commit `0472f79d9f346c9519fb43d877612a1d8dd22e1e` at
  `https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/blob/0472f79d9f346c9519fb43d877612a1d8dd22e1e/LICENSE.txt`.
- BouncyCastle.Cryptography 2.6.2 declares MIT and embeds `LICENSE.md` in the
  restored NuGet package. The distributed text is copied byte-for-text from
  that package; its repository commit is
  `b4f2f6ad76bcd1f11f365ee50cc7447fbce79077`.
- Microsoft.Bcl.Cryptography 10.0.2 and System.Security.Cryptography.Pkcs
  10.0.0 declare MIT and embed the same complete `THIRD-PARTY-NOTICES.TXT`
  (SHA-256 `6d15e10a101c6bfff2ab4429ed061bf76c456fc4b23ad6b03e0d0f8377148a21`).
  The distributed notice is copied from those restored packages. Their lock
  metadata identifies dotnet/dotnet commits
  `44525024595742ebe09023abe709df51de65009b` and
  `b0f34d51fccc69fd334253924abd8d6853fad7aa`, respectively. The MIT text is
  from `https://github.com/dotnet/dotnet/blob/44525024595742ebe09023abe709df51de65009b/LICENSE.TXT`.

NuGet metadata, package versions, repository commits, and embedded notice files
were read from the restored packages under `D:/Net_NuGet/Packages`; that cache
path is provenance only and is not included in the release payload.

## Frontend lock-file licenses

ClamAV 1.4.6 LTS is the unmodified official Windows x64 portable archive from
`https://github.com/Cisco-Talos/clamav/releases/tag/clamav-1.4.6`.
`clamav/PROVENANCE.json` records the release asset URLs and SHA-256 digests
published by the GitHub Releases API. The release payload includes the matching
complete corresponding source as `clamav/distribution/clamav-1.4.6.tar.gz`, its
detached signature, the binary archive signature and the pinned Cisco Talos
public key.

The publishing script also creates `licenses/frontend/INDEX.json` from
`web/package-lock.json` and copies root-level `LICENSE*`, `NOTICE*`, and
`COPYING*` text files from matching locally installed packages. The index marks
production/development, optional, and locally installed status. It covers the
lock file for license review and does not claim that every indexed package is
present in the production JavaScript bundle.

## OEM archive test fixtures

`tests/Oem/OemInspectionTests.cs` embeds two archive fixtures as Base64; these
are test inputs and are not included in the application runtime:

- `bla.encrypted.7z` from Apache Commons Compress, Apache License 2.0:
  `https://github.com/apache/commons-compress/blob/master/src/test/resources/bla.encrypted.7z`.
  See `licenses/Apache-2.0.txt` and `licenses/Apache-Commons-Compress-NOTICE.txt`.
- `build/testfile.rar5.rar` from `ssokolow/rar-test-files`, CC0-1.0:
  `https://github.com/ssokolow/rar-test-files/blob/master/build/testfile.rar5.rar`.
