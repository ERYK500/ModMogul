
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using StbImageSharp;
using UnityEngine;

namespace ModMogul {
  public static class TextureLoader {
    [StructLayout(LayoutKind.Explicit)]
    public readonly struct ExpirationTime {
      public enum StoredType {
        Null,
        Frames,
        Seconds
      }
      [FieldOffset(0)]
      public readonly int Frames = 0;
      [FieldOffset(0)]
      public readonly float Seconds;
      [FieldOffset(4)]
      public readonly StoredType Type = StoredType.Null;
      public ExpirationTime(int frames) {
        Frames = frames;
        Type = StoredType.Frames;
      }
      public ExpirationTime(float seconds) {
        Seconds = seconds;
        Type = StoredType.Seconds;
      }
      public static implicit operator ExpirationTime(int frames) => new(frames);
      public static implicit operator ExpirationTime(float seconds) => new(seconds);
    }
    static readonly Dictionary<string, RawTexture2D?> _textureCache = [];
    internal struct RawTexture2D {
      public readonly Color32[] Pixels = default;
      public readonly string Path = default;
      public readonly ExpirationTime ExpirationTime;
      public readonly int Width = 0, Height = 0;
      Coroutine cleanup = null;
      public RawTexture2D(Color32[] pixels, string path, int width, int height, [Optional, DefaultParameterValue(5f)] ExpirationTime expirationTime) {
        Pixels = pixels;
        Path = path;
        Width = width;
        Height = height;
        ExpirationTime = expirationTime;
        UpdateCleanup();
      }
      void UpdateCleanup() {
        if (cleanup != null) Utility.CoroutineExecutor.StopCoroutine(cleanup);
        cleanup = ExpirationTime.Type switch {
          ExpirationTime.StoredType.Frames => Utility.CoroutineExecutor.StartCoroutine(FrameTimer(this)),
          ExpirationTime.StoredType.Seconds => Utility.CoroutineExecutor.StartCoroutine(SecondsTimer(this)),
          _ => null,
        };
        IEnumerator FrameTimer(RawTexture2D t) {
          for (int f = t.ExpirationTime.Frames; f > 0; f--)
            yield return new WaitForEndOfFrame();
          _textureCache.Remove(t.Path);
        }
        IEnumerator SecondsTimer(RawTexture2D t) {
          yield return new WaitForSeconds(t.ExpirationTime.Seconds);
          _textureCache.Remove(t.Path);
        }
      }
      public Texture2D Instantiate() {
        UpdateCleanup();

        Texture2D texture = new(Width, Height, TextureFormat.RGBA32, false, false) {
          filterMode = FilterMode.Point,
          wrapMode = TextureWrapMode.Clamp
        };

        texture.SetPixels32(Pixels);
        texture.Apply(false, false);
        return texture;
      }
    }
    const string FallbackTextureName = "#@$&!";
    static RawTexture2D RawFallbackTexture {
      get {
        if (!_textureCache.ContainsKey(FallbackTextureName)) {
          Color32[] pixels = new Color32[4 * 4];
          for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
              pixels[y * 4 + x] = ((x % 2 == 0) ^ (y % 2 == 0)) ? Color.purple : Color.black;
          _textureCache.Add(FallbackTextureName, new(pixels, FallbackTextureName, 4, 4, default));
        }
        return _textureCache[FallbackTextureName] ?? default;
      }
    }
    static public Texture2D FallbackTexture => RawFallbackTexture.Instantiate();
    static public Sprite FallbackSprite {
      get {
        var tex = FallbackTexture;
        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
      }
    }
    static RawTexture2D Import(string path, bool flipVertically, ExpirationTime expirationTime) {
      // Correct order: check null/empty BEFORE File.Exists
      if (string.IsNullOrEmpty(path))
        return RawFallbackTexture;

      if (!File.Exists(path)) {
        Debug.LogError("File not found: " + path);
        return RawFallbackTexture;
      }

      byte[] data = File.ReadAllBytes(path);

      var img = ImageResult.FromMemory(data, ColorComponents.RedGreenBlueAlpha);

      // Convert RGBA bytes -> Color32[]
      var pixels = new Color32[img.Width * img.Height];
      var src = img.Data;
      for (int i = 0, p = 0; i < pixels.Length; i++, p += 4)
        pixels[i] = new Color32(src[p], src[p + 1], src[p + 2], src[p + 3]);

      if (flipVertically)
        FlipVertically(pixels, img.Width, img.Height);

      return new(pixels, path, img.Width, img.Height, expirationTime);

      static void FlipVertically(Color32[] pixels, int width, int height) {
        int row = width;
        for (int y = 0; y < height / 2; y++) {
          int top = y * row;
          int bottom = (height - 1 - y) * row;
          for (int x = 0; x < row; x++) {
            var tmp = pixels[top + x];
            pixels[top + x] = pixels[bottom + x];
            pixels[bottom + x] = tmp;
          }
        }
      }
    }
    public static IEnumerator<Texture2D> Load(string path, bool flipVertically, [Optional, DefaultParameterValue(5f)] ExpirationTime expirationTime) {
      path = Path.GetFullPath(path ?? ""); // Normalize path so the cache keeps track of consistently-pathed textures. 
      if (!_textureCache.ContainsKey(path)) { // If this texture was neither loaded or marked ...
        _textureCache.Add(path, null); // mark that this particular texture is being loaded,
        var t = Task.Run(() => { return Import(path, flipVertically, expirationTime); }); // create an asynchronous task for loading the texture data,
        while (!t.IsCompleted) yield return null; // wait fot it to complete
        _textureCache[path] = t.Result; // and populate the texture cache.
      }
      while (_textureCache[path] == null) yield return null; // If marked, wait for it to be present.
      yield return _textureCache[path]?.Instantiate();
    }
    public static IEnumerator<Texture2D> Load(string path, [Optional, DefaultParameterValue(5f)] ExpirationTime expirationTime) => Load(path, true, expirationTime);
    public static void LoadAndApply(Action<Texture2D> applyFunc, string path, bool flipVertically, [Optional, DefaultParameterValue(5f)] ExpirationTime expirationTime) {
      Utility.CoroutineExecutor.StartCoroutine(ApplyTexture());
      IEnumerator ApplyTexture() {
        var loadTexture = Load(path, flipVertically, expirationTime);
        while (loadTexture.MoveNext()) yield return null;
        applyFunc(loadTexture.Current);
      }
    }
    public static void LoadAndApply(Action<Texture2D> applyFunc, string path, [Optional, DefaultParameterValue(5f)] ExpirationTime expirationTime) => LoadAndApply(applyFunc, path, true, expirationTime);
    [Obsolete("This is just an example use of the TextureLoader.Load method; implement your own specialized implementation.")]
    public static IEnumerator ApplyTextureOnMaterial(string path, Material mat) {
      var loadTexture = Load(path);
      while (loadTexture.MoveNext()) yield return null;
      mat.mainTexture = loadTexture.Current;
    }
  }
}