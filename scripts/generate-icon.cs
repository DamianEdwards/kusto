#!/usr/bin/env dotnet
#:package SkiaSharp@3.119.0
#:property PublishAot=false

using System.Text;
using SkiaSharp;

const int designSize = 1024;
int[] iconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

var repoRoot = Directory.GetCurrentDirectory();
var projectPath = Path.Combine(repoRoot, "src", "Kusto.Cli", "Kusto.Cli.csproj");
if (!File.Exists(projectPath))
{
    throw new InvalidOperationException("Run this script from the repository root.");
}

var outputPath = Path.Combine(repoRoot, "src", "Kusto.Cli", "Assets", "kusto.ico");
var frames = iconSizes.Select(RenderPng).ToArray();

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
using var stream = File.Create(outputPath);
using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);

writer.Write((ushort)0);
writer.Write((ushort)1);
writer.Write((ushort)frames.Length);

var dataOffset = 6 + (frames.Length * 16);
foreach (var frame in frames)
{
    writer.Write((byte)(frame.Size == 256 ? 0 : frame.Size));
    writer.Write((byte)(frame.Size == 256 ? 0 : frame.Size));
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write((uint)frame.Data.Length);
    writer.Write((uint)dataOffset);
    dataOffset += frame.Data.Length;
}

foreach (var frame in frames)
{
    writer.Write(frame.Data);
}

Console.WriteLine($"Generated {Path.GetRelativePath(repoRoot, outputPath)} with {frames.Length} image sizes.");

IconFrame RenderPng(int size)
{
    var imageInfo = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var surface = SKSurface.Create(imageInfo);
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / (float)designSize);

    using var backgroundPaint = new SKPaint
    {
        IsAntialias = true,
        Shader = SKShader.CreateLinearGradient(
            new SKPoint(128, 96),
            new SKPoint(896, 928),
            [SKColor.Parse("#123B57"), SKColor.Parse("#071724")],
            [0f, 1f],
            SKShaderTileMode.Clamp)
    };
    canvas.DrawRoundRect(new SKRect(64, 64, 960, 960), 224, 224, backgroundPaint);

    using var borderPaint = new SKPaint
    {
        IsAntialias = true,
        Color = SKColor.Parse("#7DD3FC").WithAlpha(56),
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 16
    };
    canvas.DrawRoundRect(new SKRect(76, 76, 948, 948), 212, 212, borderPaint);

    using var markPaint = new SKPaint
    {
        IsAntialias = true,
        Shader = SKShader.CreateLinearGradient(
            new SKPoint(236, 220),
            new SKPoint(662, 804),
            [SKColor.Parse("#5EEAD4"), SKColor.Parse("#38BDF8")],
            [0f, 1f],
            SKShaderTileMode.Clamp),
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 112,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round
    };
    using var markPath = new SKPath();
    markPath.MoveTo(292, 248);
    markPath.LineTo(292, 776);
    markPath.MoveTo(310, 510);
    markPath.LineTo(612, 238);
    markPath.MoveTo(310, 510);
    markPath.LineTo(646, 792);
    canvas.DrawPath(markPath, markPaint);

    using var promptPaint = new SKPaint
    {
        IsAntialias = true,
        Color = SKColor.Parse("#F8FAFC"),
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 44,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round
    };
    using var promptPath = new SKPath();
    promptPath.MoveTo(448, 626);
    promptPath.LineTo(566, 724);
    promptPath.LineTo(448, 822);
    promptPath.MoveTo(606, 812);
    promptPath.LineTo(758, 812);
    canvas.DrawPath(promptPath, promptPaint);

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return new IconFrame(size, data.ToArray());
}

readonly record struct IconFrame(int Size, byte[] Data);
