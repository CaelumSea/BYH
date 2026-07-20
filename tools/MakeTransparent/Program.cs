// Matte tool: removes the baked-in checkerboard background from an icon PNG by
// flood-filling from the four edges and marking every connected near-white
// pixel as fully transparent (alpha=0).
//
// Why flood-fill and not a global color key: the subject (cream/white cloud
// hair) is the same color as the background. A simple "remove all white"
// destroys the subject. Flood-fill from the edges only clears the background
// region and stops at the subject boundary, leaving enclosed subject pixels
// intact even when they share the same color.
//
// Usage:
//   MakeTransparent <input.png> <output.png> [tolerance]
//   tolerance default = 12 (per-channel absolute delta from seed)
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: MakeTransparent <input.png> <output.png> [tolerance=12]");
    return 1;
}

string inputPath = args[0];
string outputPath = args[1];
int tolerance = args.Length > 2 && int.TryParse(args[2], out int t) ? t : 12;

using var bmp = new Bitmap(inputPath);
int w = bmp.Width;
int h = bmp.Height;

// Work in 32bpp ARGB regardless of source format.
var canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb);
using (var g = Graphics.FromImage(canvas))
{
    g.DrawImage(bmp, 0, 0, w, h);
}

var rect = new Rectangle(0, 0, w, h);
var data = canvas.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
int stride = data.Stride;
byte[] pixels = new byte[stride * h];
Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

// Pre-compute seed (average border color) for adaptive matching.
long sR = 0, sG = 0, sB = 0, sN = 0;
for (int x = 0; x < w; x++)
{
    Add(ref sR, ref sG, ref sB, ref sN, pixels, stride, x, 0);
    Add(ref sR, ref sG, ref sB, ref sN, pixels, stride, x, h - 1);
}
for (int y = 0; y < h; y++)
{
    Add(ref sR, ref sG, ref sB, ref sN, pixels, stride, 0, y);
    Add(ref sR, ref sG, ref sB, ref sN, pixels, stride, w - 1, y);
}
int seedR = (int)(sR / sN), seedG = (int)(sG / sN), seedB = (int)(sB / sN);
Console.WriteLine($"border seed avg color: R={seedR} G={seedG} B={seedB}  tolerance={tolerance}");

// Flood fill from every border pixel. BFS over 4-neighbours.
// A pixel is "background-eligible" if its RGB is within tolerance of the seed
// AND it is not already flagged transparent. We track transparency via the
// alpha byte directly (255→0).
var visited = new bool[w * h];
var queue = new Queue<int>();

// Seed the queue with all border pixels that look like background.
for (int x = 0; x < w; x++) TrySeed(x, 0);
for (int x = 0; x < w; x++) TrySeed(x, h - 1);
for (int y = 0; y < h; y++) TrySeed(0, y);
for (int y = 0; y < h; y++) TrySeed(w - 1, y);

void TrySeed(int x, int y)
{
    int idx = y * w + x;
    if (visited[idx]) return;
    int o = y * stride + x * 4;
    byte b = pixels[o + 0];
    byte g = pixels[o + 1];
    byte r = pixels[o + 2];
    if (Math.Abs(r - seedR) <= tolerance &&
        Math.Abs(g - seedG) <= tolerance &&
        Math.Abs(b - seedB) <= tolerance)
    {
        visited[idx] = true;
        queue.Enqueue(idx);
    }
}

int cleared = 0;
while (queue.Count > 0)
{
    int idx = queue.Dequeue();
    int x = idx % w;
    int y = idx / w;
    int o = y * stride + x * 4;
    pixels[o + 3] = 0; // alpha = 0 (transparent)
    cleared++;

    // 4-neighbours
    if (x > 0) EnqueueIfBg(x - 1, y);
    if (x < w - 1) EnqueueIfBg(x + 1, y);
    if (y > 0) EnqueueIfBg(x, y - 1);
    if (y < h - 1) EnqueueIfBg(x, y + 1);
}

void EnqueueIfBg(int x, int y)
{
    int nIdx = y * w + x;
    if (visited[nIdx]) return;
    int o = y * stride + x * 4;
    byte b = pixels[o + 0];
    byte g = pixels[o + 1];
    byte r = pixels[o + 2];
    if (Math.Abs(r - seedR) <= tolerance &&
        Math.Abs(g - seedG) <= tolerance &&
        Math.Abs(b - seedB) <= tolerance)
    {
        visited[nIdx] = true;
        queue.Enqueue(nIdx);
    }
}

Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
canvas.UnlockBits(data);

// Optional: feather the alpha edge one pass so the subject boundary does not
// show a hard halo against dark tray backgrounds. A simple 1px erode-then-
// average keeps it cheap and halo-free.
FeatherAlpha(canvas, stride);

// Crop tightly to the subject (the opaque bounding box) + a small margin, then
// re-center on a square canvas. This makes the subject fill ~96% of the icon
// instead of ~72% — a tray icon scaled to 16px keeps the subject legible.
Bitmap finalCanvas = CropToSubject(canvas, marginFraction: 0.02);

// Save as 32bpp PNG (preserves full alpha channel).
finalCanvas.Save(outputPath, ImageFormat.Png);

double pct = cleared * 100.0 / (w * h);
Console.WriteLine($"cleared {cleared:N0} / {w * h:N0} pixels ({pct:F1}%) → {outputPath}");
return 0;

static Bitmap CropToSubject(Bitmap src, double marginFraction)
{
    int sw = src.Width, sh = src.Height;
    // Scan for the opaque-pixel bounding box.
    int minX = sw, minY = sh, maxX = 0, maxY = 0;
    var rect = new Rectangle(0, 0, sw, sh);
    var data = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    byte[] px = new byte[data.Stride * sh];
    Marshal.Copy(data.Scan0, px, 0, px.Length);
    int st = data.Stride;
    for (int y = 0; y < sh; y++)
    {
        for (int x = 0; x < sw; x++)
        {
            if (px[y * st + x * 4 + 3] > 0) // alpha > 0
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
    }
    src.UnlockBits(data);

    int bboxW = maxX - minX + 1;
    int bboxH = maxY - minY + 1;
    if (bboxW <= 0 || bboxH <= 0)
    {
        // No opaque pixels — return source unchanged.
        return src;
    }

    // Square target side = max(bbox) + margin on all sides. Keeps aspect by
    // centering the subject; the subject fills ~92% of the square.
    int margin = (int)(Math.Max(bboxW, bboxH) * marginFraction);
    int side = Math.Max(bboxW, bboxH) + margin * 2;

    var dst = new Bitmap(side, side, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(dst))
    {
        g.Clear(Color.FromArgb(0, 0, 0, 0)); // fully transparent
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
        // Center the bbox in the square.
        int dx = (side - bboxW) / 2;
        int dy = (side - bboxH) / 2;
        g.DrawImage(src, new Rectangle(dx, dy, bboxW, bboxH),
            new Rectangle(minX, minY, bboxW, bboxH), GraphicsUnit.Pixel);
    }
    Console.WriteLine($"crop: bbox {bboxW}x{bboxH} → square {side}x{side} (subject ~{Math.Max(bboxW, bboxH) * 100.0 / side:N0}% fill)");
    return dst;
}

static void Add(ref long sR, ref long sG, ref long sB, ref long sN, byte[] px, int stride, int x, int y)
{
    int o = y * stride + x * 4;
    sR += px[o + 2];
    sG += px[o + 1];
    sB += px[o + 0];
    sN++;
}

static void FeatherAlpha(Bitmap bmp, int stride)
{
    // One-pass edge feather: for any transparent pixel that borders an opaque
    // pixel, leave it; for opaque pixels directly adjacent to transparent,
    // multiply alpha by 0.7 to soften the silhouette edge. Cheap and avoids
    // the "white ring" artifact on dark backgrounds.
    var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
    var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    byte[] px = new byte[data.Stride * bmp.Height];
    Marshal.Copy(data.Scan0, px, 0, px.Length);
    int st = data.Stride;
    int w = bmp.Width, h = bmp.Height;
    // Make a copy of original alpha so the feather pass is order-independent.
    byte[] origA = new byte[w * h];
    for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
            origA[y * w + x] = px[y * st + x * 4 + 3];

    for (int y = 1; y < h - 1; y++)
    {
        for (int x = 1; x < w - 1; x++)
        {
            int o = y * st + x * 4;
            if (origA[y * w + x] == 0) continue; // already transparent
            // If any 4-neighbour is transparent, this is a silhouette edge.
            bool edge = origA[(y - 1) * w + x] == 0 ||
                        origA[(y + 1) * w + x] == 0 ||
                        origA[y * w + (x - 1)] == 0 ||
                        origA[y * w + (x + 1)] == 0;
            if (edge)
            {
                px[o + 3] = (byte)(px[o + 3] * 0.7);
            }
        }
    }
    Marshal.Copy(px, 0, data.Scan0, px.Length);
    bmp.UnlockBits(data);
}
