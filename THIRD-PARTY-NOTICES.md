# Third-party components

CryptoSSH source is licensed under MIT; see LICENSE. The following redistributed dependencies retain their respective licenses.

| Component | Version | License / included notice | Upstream |
| --- | --- | --- | --- |
| SSH.NET | 2026.0.0 | MIT, `licenses/SSH.NET.txt` | https://github.com/sshnet/SSH.NET |
| Pkcs11Interop | 5.3.0 | Apache-2.0, `licenses/Pkcs11Interop.txt` | https://github.com/Pkcs11Interop/Pkcs11Interop |
| BouncyCastle.Cryptography | 2.7.0 | MIT-style, `licenses/BouncyCastle.txt` | https://github.com/bcgit/bc-csharp |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT, `licenses/Microsoft.Extensions-MIT.txt`, `licenses/Microsoft.Extensions-NOTICES.txt` | https://github.com/dotnet/runtime |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.2 | Same Microsoft notices above | https://github.com/dotnet/runtime |
| .NET 10 Windows Desktop runtime | Build-selected patch recorded in release inventory | MIT and third-party notices, `licenses/dotnet-MIT.txt`, `licenses/dotnet-THIRD-PARTY-NOTICES.txt` | https://github.com/dotnet/wpf and https://github.com/dotnet/runtime |

The terminal renderer and VT screen implementation are part of CryptoSSH. No Windows Terminal, ConPTY, OpenSSH, OpenSC, Rutoken or JaCarta binaries are distributed.

Rutoken and JaCarta drivers / PKCS#11 libraries are optional external prerequisites, obtained and installed by the user under the manufacturer's terms. CryptoSSH does not grant redistribution rights to these components. Rutoken is a product of Aktiv; JaCarta is a product of Aladdin R.D. CryptoSSH is independent of both vendors.

NSIS builds the installer; its licensing information is available at https://nsis.sourceforge.io/License . SoftHSM and ssh2 are development-only test dependencies and are not included in the application release. See their upstream licenses when installing test tooling: https://github.com/softhsm/SoftHSMv2 and https://github.com/mscdex/ssh2 .
