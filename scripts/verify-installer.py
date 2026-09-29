"""Regression check for the actual NSIS license data (Python standard library only)."""
from pathlib import Path
import lzma
import sys

root = Path(__file__).resolve().parents[1]
installer = Path(sys.argv[1]) if len(sys.argv) > 1 else root / "release/CryptoSSH-1.0.0-Setup-x64.exe"
data = installer.read_bytes()
header = data.index(b"\xef\xbe\xad\xdeNullsoftInst") - 4
position = header + 28
properties = data[position]
lc, remainder = properties % 9, properties // 9
lp, pb = remainder % 5, remainder // 5
decoder = lzma.LZMADecompressor(format=lzma.FORMAT_RAW, filters=[{
    "id": lzma.FILTER_LZMA1,
    "dict_size": int.from_bytes(data[position + 1:position + 5], "little"),
    "lc": lc, "lp": lp, "pb": pb,
}])
# LicenseData follows the NSIS header, before the bundled application payload.
content = decoder.decompress(data[position + 5:], max_length=1024 * 1024)
license_text = (root / "INSTALL-LICENSE.txt").read_text(encoding="utf-8-sig")
for line in license_text.splitlines():
    if line and line.encode("utf-16le") not in content:
        raise SystemExit("FAIL: license text is not intact UTF-16 in the compiled installer")
print("PASS: every license paragraph is intact Unicode in the compiled NSIS installer")
