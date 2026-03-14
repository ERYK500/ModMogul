using UnityEngine;
using System;
using System.Threading;
using System.Collections;
using System.Reflection;

namespace ModMogul
{
	public class Utility
	{
		public class DummyMonoBehaviour : MonoBehaviour
		{
      internal static DummyMonoBehaviour _coroutineExecutor;
    }
    public static DummyMonoBehaviour CoroutineExecutor
		{
			get
			{
				if (DummyMonoBehaviour._coroutineExecutor == null)
					DummyMonoBehaviour._coroutineExecutor = new GameObject("ModMogul_CoroutineExecutor").AddComponent<DummyMonoBehaviour>();
				return DummyMonoBehaviour._coroutineExecutor;
			}
		}
		internal static void SetSprite<T>(T obj, string member, string path, bool flipVertically = true)
		{
			const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
			var fi = typeof(T).GetField(member, flags);
			var pi = typeof(T).GetProperty(member, flags);
			if (fi == null && pi == null) {
				Debug.Log("Can't locate member '" + member + "' of type '" + typeof(T) + "'!\nSkipping assignment of Sprite ...");
				return;
			}
			if ((typeof(Sprite) != fi?.FieldType) && (typeof(Sprite) != pi?.PropertyType)) {
				Debug.Log("Member '" + member + "' is of type '" + fi.FieldType + "' and not of type 'Sprite'!\nSkipping assignment of Sprite ...");
				return;
			}
			CoroutineExecutor.StartCoroutine(pi == null ? SetField() : SetProperty());
			IEnumerator SetField() {
				var loadTexture = TextureLoader.Load(path, flipVertically);
				while (loadTexture.MoveNext()) yield return loadTexture.Current;
				var tex = loadTexture.Current;
				fi.SetValue(obj, Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f));
			}
			IEnumerator SetProperty() {
				var loadTexture = TextureLoader.Load(path, flipVertically);
				while (loadTexture.MoveNext()) yield return loadTexture.Current;
				var tex = loadTexture.Current;
				pi.SetValue(obj, Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f));
			}
		}
	}
}
