using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GLTFast.Logging;
using GLTFast.Newtonsoft.Schema;
using UnityEngine;


// IMPORTANT: force the Newtonsoft importer
using GltfImport = GLTFast.Newtonsoft.GltfImport;

namespace ModMogul
{
	public static class ModelMaker
	{
		[Serializable]
		public enum MaterialType { Opaque, Fade, Cutout }

		/// <summary>
		/// Loads a GLB/GLTF from disk and instantiates its default scene under parent.
		/// Returns true on success.
		/// </summary>
		internal static async Task<GltfImport> LoadModelAsync(
			string modelPath,
			Transform parent,
			CancellationToken ct = default)
		{
			if (parent == null)
			{
				Debug.LogError("LoadModelAsync: parent is null");
				return null;
			}

			try
			{
				var fullPath = Path.GetFullPath(modelPath);
				if (!File.Exists(fullPath))
				{
					Debug.LogError($"LoadModelAsync: file not found: {fullPath}");
					return null;
				}

				// Build a correct file:// URI (don’t concatenate strings)
				var uri = new Uri(fullPath);

				var logger = new CollectingLogger();

				// If you later want custom materials, pass materialGenerator: new SafeBuiltinMaterialGenerator()
				var gltfImport = new GltfImport(logger: logger);

				Debug.Log($"glTFast Load: {uri}");

				var ok = await gltfImport.Load(uri, cancellationToken: ct);
				if (!ok)
				{
					Debug.LogError($"glTFast failed to load: {fullPath}");
					logger.LogAll();
					return null;
				}

				// Instantiate under parent
				ok = await gltfImport.InstantiateMainSceneAsync(parent, cancellationToken: ct);
				if (!ok)
				{
					Debug.LogError($"glTFast failed to instantiate: {fullPath}");
					logger.LogAll();
					return null;
				}

				return gltfImport;
			}
			catch (OperationCanceledException)
			{
				Debug.LogWarning("LoadModelAsync canceled");
				return null;
			}
			catch (Exception e)
			{
				Debug.LogException(e);
				return null;
			}
		}

		public static async Task<GameObject> LoadModelToGameObjectAsync(
			string modelPath,
			Transform parent,
			MaterialType materialType,
			string childName = null,
			CancellationToken ct = default)
		{
			var rootGo = new GameObject(childName ?? Path.GetFileNameWithoutExtension(modelPath));
			rootGo.transform.SetParent(parent, false);

			var glbImport = await LoadModelAsync(modelPath, rootGo.transform, ct);
			if (glbImport == null)
			{
				UnityEngine.Object.Destroy(rootGo);
				return null;
			}
			// Poke each renderer with a stick so it initializes.  Yea, this was a whole ordeal to figure out.
			for (int i = 0; i < rootGo.GetComponentsInChildren<MeshRenderer>().Length; i++)
			{
				MeshRenderer m = rootGo.GetComponentsInChildren<MeshRenderer>()[i];
				var _ = m.materials;
			}
			BindAllToStandard(glbImport, rootGo, materialType);

			return rootGo;
		}

		public static void BindAllToStandard(GltfImport import, GameObject instantiatedRoot, MaterialType materialType)
		{
			if (import == null || instantiatedRoot == null) return;

			var gltf = import.GetSourceRoot();
			if (gltf == null)
			{
				Debug.LogError("BindAllToStandard: GetSourceRoot() returned null");
				return;
			}

			// 1) Build Standard material for each glTF material index
			var standardTemplate = CreateCleanStandard();
			if (standardTemplate == null || standardTemplate.shader == null)
			{
				Debug.LogError("BindAllToStandard: could not create/find Standard material");
				return;
			}

			var stdByGltfMat = BuildStandardMaterials(import, gltf, standardTemplate, materialType);

			// 2) Build lookup: nodeName -> nodeIndex
			var nodeIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);
			if (gltf.nodes != null)
			{
				for (int i = 0; i < gltf.nodes.Length; i++)
				{
					var n = gltf.nodes[i];
					if (!string.IsNullOrEmpty(n.name) && !nodeIndexByName.ContainsKey(n.name))
						nodeIndexByName[n.name] = i;
				}
			}

			// 3) For each MeshRenderer, map by name -> glTF node -> mesh -> primitives -> material indices
			foreach (var r in instantiatedRoot.GetComponentsInChildren<MeshRenderer>(true))
			{
				// Force realization (avoids your “pink until poke” issue)
				var mats = r.materials;

				// Try match renderer name to glTF node name
				if (!nodeIndexByName.TryGetValue(r.name, out var nodeIndex))
				{
					// fallback: sometimes instantiated GO has "(Clone)" or extra suffix
					var trimmed = TrimCloneSuffix(r.name);
					if (!nodeIndexByName.TryGetValue(trimmed, out nodeIndex))
						continue;
				}

				var node = gltf.nodes[nodeIndex];
				//if (!node.mesh.HasValue) continue;

				int meshIndex = node.mesh;
				if (gltf.meshes == null || meshIndex < 0 || meshIndex >= gltf.meshes.Length) continue;

				var mesh = gltf.meshes[meshIndex];
				var prims = mesh.primitives;
				if (prims == null || prims.Length == 0) continue;

				// Ensure material slot count matches primitive count (Unity uses submesh/material slot)
				if (mats == null || mats.Length != prims.Length)
					Array.Resize(ref mats, prims.Length);

				bool changed = false;

				for (int slot = 0; slot < prims.Length; slot++)
				{
					int gltfMatIndex = prims[slot].material;

					// glTF primitive material can be null; use default
					var std = stdByGltfMat.TryGetValue(gltfMatIndex, out var mat) ? mat : stdByGltfMat[-1];

					if (mats[slot] != std)
					{
						mats[slot] = std;
						changed = true;
					}
				}

				if (changed)
				{
					r.materials = mats;
					r.SetPropertyBlock(null);
				}
			}

			// 3) For each MeshRenderer, map by name -> glTF node -> mesh -> primitives -> material indices
			foreach (var r in instantiatedRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
			{
				// Force realization (avoids your “pink until poke” issue)
				var mats = r.materials;

				// Try match renderer name to glTF node name
				if (!nodeIndexByName.TryGetValue(r.name, out var nodeIndex))
				{
					// fallback: sometimes instantiated GO has "(Clone)" or extra suffix
					var trimmed = TrimCloneSuffix(r.name);
					if (!nodeIndexByName.TryGetValue(trimmed, out nodeIndex))
						continue;
				}

				var node = gltf.nodes[nodeIndex];
				//if (!node.mesh.HasValue) continue;

				int meshIndex = node.mesh;
				if (gltf.meshes == null || meshIndex < 0 || meshIndex >= gltf.meshes.Length) continue;

				var mesh = gltf.meshes[meshIndex];
				var prims = mesh.primitives;
				if (prims == null || prims.Length == 0) continue;

				// Ensure material slot count matches primitive count (Unity uses submesh/material slot)
				if (mats == null || mats.Length != prims.Length)
					Array.Resize(ref mats, prims.Length);

				bool changed = false;

				for (int slot = 0; slot < prims.Length; slot++)
				{
					int gltfMatIndex = prims[slot].material;

					// glTF primitive material can be null; use default
					var std = stdByGltfMat.TryGetValue(gltfMatIndex, out var mat) ? mat : stdByGltfMat[-1];

					if (mats[slot] != std)
					{
						mats[slot] = std;
						changed = true;
					}
				}

				if (changed)
				{
					r.materials = mats;
					r.SetPropertyBlock(null);
				}
			}
		}

		private static Dictionary<int, UnityEngine.Material> BuildStandardMaterials(GltfImport import, Root gltf, UnityEngine.Material template, MaterialType materialType)
		{
			var dict = new Dictionary<int, UnityEngine.Material>();

			// Default/fallback material at key -1
			var def = new UnityEngine.Material(template) { name = "glTF_Default_Standard" };
			dict[-1] = def;

			if (gltf.materials == null || gltf.materials.Length == 0)
				return dict;

			for (int mi = 0; mi < gltf.materials.Length; mi++)
			{
				// BaseColorFactor -> _Color
				var gm = gltf.materials[mi];
				var pbr = gm.pbrMetallicRoughness;
				var m = new UnityEngine.Material(template)
				{
					name = string.IsNullOrEmpty(gm.name) ? $"glTF_Mat_{mi}_Standard" : $"{gm.name}_Standard"
				};

				UnityEngine.Texture tex = null;

				int texIndex = -1;
				if (pbr?.baseColorTexture != null) texIndex = pbr.baseColorTexture.index;

				// Primary path: use baseColorTexture.index
				if (texIndex >= 0)
					tex = import.GetTexture(texIndex);

				// Fallback: if glTF has exactly one texture, use it
				if (tex == null && gltf.textures != null && gltf.textures.Length == 1)
					tex = import.GetTexture(0);

				if (tex != null && m.HasProperty("_MainTex"))
					m.SetTexture("_MainTex", tex);

				// DoubleSided -> disable cull
				if (gm.doubleSided)
					m.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);

				// Transparency handling (glTF-driven; fast + correct)
				switch (materialType)
				{
					case MaterialType.Fade:
						ApplyBlendMaterial(m, tex);
						//SetStandardFade(m);
						break;

					case MaterialType.Cutout:
						SetStandardCutout(m, 0.5f);
						break;

					default: // OPAQUE or null
						SetStandardOpaque(m);
						break;
				}

				if (pbr != null && m.HasProperty("_Color") && pbr.baseColorFactor != null && pbr.baseColorFactor.Length >= 4)
				{
					m.SetColor("_Color", new Color(
						pbr.baseColorFactor[0],
						pbr.baseColorFactor[1],
						pbr.baseColorFactor[2],
						pbr.baseColorFactor[3]
					));
				}

				ApplyEmissive(import, gm, m);

				dict[mi] = m;
			}

			return dict;
		}

		private static void ApplyEmissive(GltfImport import, GLTFast.Newtonsoft.Schema.Material gltfMaterial, UnityEngine.Material unityMaterial)
		{
			if (import == null || gltfMaterial == null || unityMaterial == null)
			{
				return;
			}

			UnityEngine.Texture emissiveTex = null;
			if (gltfMaterial.emissiveTexture != null && gltfMaterial.emissiveTexture.index >= 0)
			{
				emissiveTex = import.GetTexture(gltfMaterial.emissiveTexture.index);
			}

			if (emissiveTex != null && unityMaterial.HasProperty("_EmissionMap"))
			{
				unityMaterial.SetTexture("_EmissionMap", emissiveTex);
			}

			float r = 0f;
			float g = 0f;
			float b = 0f;
			if (gltfMaterial.emissiveFactor != null && gltfMaterial.emissiveFactor.Length >= 3)
			{
				r = gltfMaterial.emissiveFactor[0];
				g = gltfMaterial.emissiveFactor[1];
				b = gltfMaterial.emissiveFactor[2];
			}

			bool hasEmission = emissiveTex != null || r > 0f || g > 0f || b > 0f;
			if (!hasEmission || !unityMaterial.HasProperty("_EmissionColor"))
			{
				return;
			}

			if (r <= 0f && g <= 0f && b <= 0f)
			{
				r = 1f;
				g = 1f;
				b = 1f;
			}

			unityMaterial.SetColor("_EmissionColor", new Color(r, g, b, 1f));
			unityMaterial.globalIlluminationFlags &= ~MaterialGlobalIlluminationFlags.EmissiveIsBlack;
			unityMaterial.globalIlluminationFlags |= MaterialGlobalIlluminationFlags.RealtimeEmissive;
			unityMaterial.EnableKeyword("_EMISSION");
		}

		static UnityEngine.Material CreateCleanStandard()
		{
			var sh = Shader.Find("Standard");
			if (sh == null) sh = Shader.Find("Unlit/Texture");
			var m = new UnityEngine.Material(sh);

			// Clear any texture slots we might set later (defensive)
			if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", null);
			if (m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", null);
			if (m.HasProperty("_EmissionMap")) m.SetTexture("_EmissionMap", null);
			if (m.HasProperty("_MetallicGlossMap")) m.SetTexture("_MetallicGlossMap", null);
			if (m.HasProperty("_OcclusionMap")) m.SetTexture("_OcclusionMap", null);
			if (m.HasProperty("_DetailAlbedoMap")) m.SetTexture("_DetailAlbedoMap", null);
			if (m.HasProperty("_DetailNormalMap")) m.SetTexture("_DetailNormalMap", null);

			// Disable common keywords that cause “ghost” effects
			m.DisableKeyword("_EMISSION");
			m.DisableKeyword("_NORMALMAP");
			m.DisableKeyword("_METALLICGLOSSMAP");
			m.DisableKeyword("_PARALLAXMAP");
			m.DisableKeyword("_DETAIL_MULX2");
			m.DisableKeyword("_DETAIL_SCALED");

			// Reset key floats/colors
			if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
			if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
			if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.5f);
			if (m.HasProperty("_BumpScale")) m.SetFloat("_BumpScale", 1f);
			if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);

			return m;
		}

		private static string TrimCloneSuffix(string name)
		{
			const string clone = "(Clone)";
			if (name != null && name.EndsWith(clone, StringComparison.Ordinal))
				return name.Substring(0, name.Length - clone.Length).TrimEnd();
			return name;
		}

		static void SetStandardOpaque(UnityEngine.Material m)
		{
			m.SetOverrideTag("RenderType", "Opaque");
			m.SetFloat("_Mode", 0);
			m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
			m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
			m.SetInt("_ZWrite", 1);
			m.DisableKeyword("_ALPHATEST_ON");
			m.DisableKeyword("_ALPHABLEND_ON");
			m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
			m.renderQueue = -1;
		}

		static void SetStandardCutout(UnityEngine.Material m, float cutoff)
		{
			m.SetOverrideTag("RenderType", "TransparentCutout");
			m.SetFloat("_Mode", 1);
			if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", cutoff);
			m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
			m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
			m.SetInt("_ZWrite", 1);
			m.EnableKeyword("_ALPHATEST_ON");
			m.DisableKeyword("_ALPHABLEND_ON");
			m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
			m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
		}

		static void SetStandardFade(UnityEngine.Material m)
		{
			m.shader = ModMogulPlugin.fadeShader;
			/*
			m.SetOverrideTag("RenderType", "Transparent");
			m.SetFloat("_Mode", 2f);
			m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
			m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
			m.SetInt("_ZWrite", 0);
			m.DisableKeyword("_ALPHATEST_ON");
			m.EnableKeyword("_ALPHABLEND_ON");
			m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
			m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
			*/
		}

		static Shader FindFirst(params string[] names)
		{
			foreach (var n in names)
			{
				var s = Shader.Find(n);
				if (s != null) return s;
			}
			return null;
		}

		static void ApplyBlendMaterial(UnityEngine.Material m, UnityEngine.Texture mainTex)
		{
			// Try Standard Fade first
			var standard = Shader.Find("Standard");
			if (standard != null)
			{
				m.shader = standard;
				if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", mainTex);
				SetStandardFade(m);

				// Heuristic: if keyword sticks, assume it works. (Not foolproof, but cheap.)
				if (m.IsKeywordEnabled("_ALPHABLEND_ON"))
					return;
			}

			// Try any available transparent shader
			var transparent = FindFirst(
				"Unlit/Transparent",
				"Legacy Shaders/Transparent/Diffuse",
				"Legacy Shaders/Transparent/VertexLit",
				"Particles/Standard Unlit",
				"Sprites/Default"
			);

			if (transparent != null)
			{
				m.shader = transparent;

				// Common property name for these
				if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", mainTex);
				if (m.HasProperty("_Color"))
				{
					var c = m.GetColor("_Color");
					c.a = 1f; // texture alpha drives
					m.SetColor("_Color", c);
				}

				// For many transparent shaders, renderQueue helps
				m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
				return;
			}

			// Last resort: cutout
			SetStandardCutout(m, 0.5f);
			if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", mainTex);
		}
	}
}
