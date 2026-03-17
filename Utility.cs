using UnityEngine;

namespace ModMogul
{
	public static class Utility
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
		internal static Sprite ToIconSprite(this Texture2D tex) {
			return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
		}
	}
}
