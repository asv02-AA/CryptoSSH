from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
image = Image.new('RGBA', (256, 256), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((8, 8, 248, 248), radius=56, fill='#8194eb')
draw.rounded_rectangle((49, 62, 207, 194), radius=20, outline='#101728', width=12)
draw.line((77, 103, 103, 125, 77, 147), fill='#101728', width=12, joint='curve')
draw.line((119, 150, 164, 150), fill='#101728', width=12)
image.save(root / 'native' / 'CryptoSSH' / 'app.ico', sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
