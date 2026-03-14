using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using UnityEngine;
using Unity.VisualScripting;
using HarmonyLib;
using Newtonsoft.Json;

namespace ModMogul {
  public static class OrePieceCreator {
    /// <summary>
    /// A special class that's used to store the mapping of what PieceTypes & ResourceTypes,
    ///   of any particular name had which Ids, so when the game loads a save
    ///   where the Ids were mapped differently than was currently loaded,
    ///   we can easily fix the ordering as the OrePieces get loaded.
    /// </summary>
    internal class ResourceAndPieceTypeDictionary : MonoBehaviour, ISaveLoadableObject {
      internal static Dictionary<PieceType, PieceType> PieceTypeMap = [];
      internal static Dictionary<ResourceType, ResourceType> ResourceTypeMap = [];
      ~ResourceAndPieceTypeDictionary() {
        PieceTypeMap = [];
        ResourceTypeMap = [];
      }
      public bool HasBeenSaved { get => false; set { } }
      public string GetCustomSaveData() {
        StringBuilder sb = new();

        using (JsonWriter writer = new JsonTextWriter(new StringWriter(sb))) {
          writer.WriteStartObject();
          writer.WritePropertyName("PieceTypes");
          writer.WriteStartObject();
          AllPieceTypes.ForEach((e) => {
            writer.WritePropertyName(e.Value);
            writer.WriteValue((int)e.Key);
          });
          writer.WriteEndObject();
          writer.WritePropertyName("ResourceTypes");
          writer.WriteStartObject();
          AllResourceTypes.ForEach((e) => {
            writer.WritePropertyName(e.Value);
            writer.WriteValue((int)e.Key);
          });
          writer.WriteEndObject();
          writer.WriteEndObject();
        }

        return sb.ToString();
      }
      public Vector3 GetPosition() {
        return Vector3.zero;
      }
      public Vector3 GetRotation() {
        return Vector3.zero;
      }
      public SavableObjectID GetSavableObjectID() {
        return (SavableObjectID)(-1);
      }
      interface IHelper {
        public void StoreTmp(string entry);
        public void StoreMapping(long? entry);
      }
      class Helper<T> : IHelper where T : Enum {
        public ISearchable<T> registry;
        public IDictionary<T, T> typeMap;
        public T tmp;
        public void StoreTmp(string entry) {
          tmp = registry.Find(entry);
        }
        public void StoreMapping(long? entry) {
          //Debug.Log(entry + " => " + registry[tmp]);
          var val = (T)(object)(int)(entry ?? 0L);
          if (!EqualityComparer<T>.Default.Equals(val, tmp)) typeMap.Add(val, tmp);
        }
      };
      public void LoadFromSave(string customDataJson) {
        var rdr = new JsonTextReader(new StringReader(customDataJson));
        IHelper helper = null;
        while (rdr.Read()) {
          switch (rdr.TokenType) {
            case JsonToken.PropertyName:
              if (helper == null) {
                switch (rdr.Value as string) {
                  case "PieceTypes":
                    helper = new Helper<PieceType>{ registry = AllPieceTypes, typeMap = PieceTypeMap, tmp = PieceType.INVALID };
                    break;
                  case "ResourceTypes":
                    helper = new Helper<ResourceType>{ registry = AllResourceTypes, typeMap = ResourceTypeMap, tmp = ResourceType.INVALID };
                    break;
                  default:
                    Debug.Log(string.Format("Encountered an illegal: {0} top-level field! This couldn't be read, therefore the rest of this process will be skipped! This may cause an incorrect PieceType & ResourceType translation/remapping!", rdr.Value));
                    return;
                }
                break;
              } else helper.StoreTmp(rdr.Value as string);
              break;
            case JsonToken.Integer:
              if (helper == null) break; // It's not expected that top-level fields have integer values, skipping it's processing.
              helper.StoreMapping(rdr.Value as long?);
              break;
            case JsonToken.EndObject:
              helper = null;
              break;
            case JsonToken.StartObject:
              break;
            default:
              if (rdr.Value != null) {
                Debug.Log(string.Format("Unsupported token: {0}, Value: {1}", rdr.TokenType, rdr.Value));
              } else {
                Debug.Log(string.Format("Unsupported token: {0}", rdr.TokenType));
              }
              break;
          }
        }
      }
      public bool ShouldBeSaved() {
        return true;
      }
    }
    public readonly struct NameEntry {
      internal static string ConvertToInternalName(string name) {
        return Regex.Replace(name, "\\s", "").ToLower();
      }
      public readonly string PrettyName;
      public readonly string InternalName;
      public NameEntry(string name) {
        PrettyName = name;
        InternalName = ConvertToInternalName(name);
      }
    }
    /// <summary>
    /// This is an internal interface that's used for the simplification of the
    ///   parsing of save data stored by the "ResourceAndPieceTypeDictionary".
    /// </summary>
    /// <typeparam name="T">Either "PieceType" or "ResourceType".</typeparam>
    interface ISearchable<T> {
      //public string this[T idx] { get; }
      public T Find(string name);
    }
    public class AllPieceTypesT : ISearchable<PieceType> {
      /// <summary>
      /// Stores all PieceType names.
      /// </summary>
      internal Dictionary<PieceType, NameEntry> _Types;
      internal AllPieceTypesT() {
        _Types = new(Enum.GetValues(typeof(PieceType)).Length);
        foreach (var v in Enum.GetValues(typeof(PieceType)).ConvertTo<List<PieceType>>()) {
          var tmp = Enum.GetName(typeof(PieceType), v);
          _Types.Add(v, new NameEntry(tmp));
        }
      }
      /// <summary>
      /// Get or set a PieceType's name.
      /// </summary>
      public string this[PieceType idx] {
        get { return _Types[idx].PrettyName; }
        internal set {
          _Types.Add(idx, new NameEntry(value));
        }
      }
      /// <summary>
      /// Find the Id of the provided PieceType.
      /// </summary>
      public PieceType Find(string name) {
        var internalizedName = NameEntry.ConvertToInternalName(name);
        return _Types.ToList().Find((e) => e.Value.InternalName == internalizedName).Key;
      }
      internal void ForEach(Action<KeyValuePair<PieceType, string>> a) {
        _Types.Select((e) => new KeyValuePair<PieceType, string>(e.Key, e.Value.PrettyName)).ToList().ForEach(a);
      }
    }
    public static AllPieceTypesT AllPieceTypes = new();
    public class AllResourceTypesT : ISearchable<ResourceType> {
      /// <summary>
      /// Stores all ResourceType names.
      /// </summary>
      internal Dictionary<ResourceType, NameEntry> _Types;
      internal AllResourceTypesT() {
        _Types = new(Enum.GetValues(typeof(ResourceType)).Length);
        foreach (var v in Enum.GetValues(typeof(ResourceType)).ConvertTo<List<ResourceType>>()) {
          var tmp = Enum.GetName(typeof(ResourceType), v);
          _Types.Add(v, new NameEntry(tmp));
        }
      }
      /// <summary>
      /// Get or set a ResourceType's name.
      /// </summary>
      public string this[ResourceType idx] {
        get { return _Types[idx].PrettyName; }
        internal set {
          _Types.Add(idx, new NameEntry(value));
        }
      }
      /// <summary>
      /// Find the Id of the provided ResourceType.
      /// </summary>
      public ResourceType Find(string name) {
        var internalizedName = NameEntry.ConvertToInternalName(name);
        return _Types.ToList().Find((e) => e.Value.InternalName == internalizedName).Key;
      }
      internal void ForEach(Action<KeyValuePair<ResourceType, string>> a) {
        _Types.Select((e) => new KeyValuePair<ResourceType, string>(e.Key, e.Value.PrettyName)).ToList().ForEach(a);
      }
    }
    public static AllResourceTypesT AllResourceTypes = new();
    public class MeltableMaskT {
      /// <summary>
      /// Stores which ResourceType is "meltable" in an extendable bitmask format.
      /// </summary>
      internal List<int> _Mask;
      internal MeltableMaskT() {
        int mask = 0;
        foreach (var type in new ResourceType[] { ResourceType.Iron, ResourceType.Gold, ResourceType.Copper, ResourceType.Steel }) {
          if ((int)type > 0x1F) throw new IndexOutOfRangeException("The provided resource type: " + type + " has a value of " + (int)type + " which exceeds the size of the default integer!\nEither this is an incorrect definition or the implementation needs to be updated to initialize such entries.");
          mask |= 1 << (int)type;
        }
        _Mask = [mask];
      }
      /// <summary>
      /// Get or set which ResourceType is "meltable" from/in '_Mask'.
      /// </summary>
      public bool this[ResourceType idx] {
        get { return (_Mask[(int)idx >> 5] & (1 << ((int)idx & 0x1F))) != 0; }
        internal set {
          var tmp = 1 << ((int)idx & 0x1F);
          if (((int)idx >> 5) >= _Mask.Count) _Mask.Insert((int)idx >> 5, 0);
          if (value) _Mask[(int)idx >> 5] |= tmp;
          else _Mask[(int)idx >> 5] &= ~tmp;
        }
      }
    }
    public static MeltableMaskT MeltableMask = new();
    private static GameObject _prefabHolder;
    /// <summary>
    /// This holds the reference to the GameObject responsible for
    ///   holding custom prefabs & keeping them loaded at all times.
    /// </summary>
    internal static GameObject PrefabHolder {
      get {
        if (_prefabHolder != null) return _prefabHolder;
        _prefabHolder = new GameObject("ModMogul_OrePieceCreator_PrefabHolder");
        _prefabHolder.transform.position = -Vector3.up * 1000;
        UnityEngine.Object.DontDestroyOnLoad(_prefabHolder);
        _prefabHolder.hideFlags = HideFlags.HideAndDontSave;
        _prefabHolder.SetActive(false);
        return _prefabHolder;
      }
    }
    /// <summary>
    /// Used for adding ResourceType Color mappings to the OreManager
    ///  when it gets finally loaded.
    /// </summary>
    private static readonly List<ResourceDescription> _addedResourceDescriptions = [];
    /// <summary>
    /// This is called when the new PieceTypes, ResourceTypes & their prefabs are supposed to be added/created.<br/>
    /// You are not allowed to add new PieceType & ResourceType definitions after this event finishes.
    /// </summary>
    /// <param name="PrefabHolder">The object that is used to hold prefabs & keep them loaded.</param>
    public static event Action<GameObject> OnPieceAndResourceTypeCreation = new((_) => { });
    /// <summary>
    /// This tracks if all of the potential new PieceType & ResourceType definitions were added or not.
    /// </summary>
    static bool _initialized = false;
    /// <summary>
    /// This is called after the new PieceTypes, ResourceTypes & their prefabs are supposed to be added/created.<br/>
    /// You are not allowed to add new PieceType & ResourceType definitions inside of this event.<br/>
    /// The main reason that this exists, is to be able to read the now finalized definitions that can't change anymore.
    /// </summary>
    public static event Action AfterPieceAndResourceTypeCreation = new(() => { });
    /// <summary>
    /// The following is used to apply new textures to a provided material.
    /// </summary>
    /// <param name="mat">The material to color and re-texture.</param>
    /// <param name="originalName">The name of the original material that the prefabs were copied from.</param>
    /// <param name="prefabName">The prefab name of the new material that these prefabs belong to.</param>
    /// <param name="path">The path to the texture file to import. If null a placeholder texture will be applied.</param>
    static IEnumerator ApplyTextureImpl(Material mat, string originalName, string prefabName, string path) {
      var loadTexture = TextureLoader.Load(path);
      while (loadTexture.MoveNext()) yield return null; // Wait for it to be loaded.
      mat.name = Regex.Replace(mat.name, originalName, prefabName);
      var texName = Regex.Replace(mat.name, originalName, prefabName);
      mat.mainTexture = loadTexture.Current;
      mat.mainTexture.name = texName;
    }
    /// <summary>
    /// The following is used to automatically apply new default textures to a provided material,
    ///   such that they match with the default meshes of given OrePiece prefab.
    /// </summary>
    /// <param name="piece">The OrePiece prefab that the Material mat belongs to.</param>
    /// <param name="mat">The material to color and re-texture.</param>
    /// <param name="originalName">The name of the original material that the prefabs were copied from.</param>
    /// <param name="prefabName">The prefab name of the new material that these prefabs belong to.</param>
    static IEnumerator ApplyDefaultTextureImpl(OrePiece piece, Material mat, string originalName, string prefabName) {
      static IEnumerator DummyEnumerator() {
        yield break;
      }
      var resType = AllResourceTypes.Find(originalName);
      if ((resType == ResourceType.Gold) && (piece.PieceType == PieceType.ThreadedRod)) {
        return ApplyTextureImpl(mat, originalName, prefabName, ModMogulPlugin.ModMogulPath + "\\OrePieceCreator Defaults\\Whitened_Ruined_ThreadedRods.png");
      }
      if (new List<ResourceType> { ResourceType.Gold, ResourceType.Slag }.Contains(resType) && (piece.PieceType == PieceType.Pipe)) {
        return ApplyTextureImpl(mat, originalName, prefabName, ModMogulPlugin.ModMogulPath + "\\OrePieceCreator Defaults\\Whitened_Special_Pipes.png");
      }
      if (resType == ResourceType.Slag) {
        return DummyEnumerator();
      }
      if (new List<ResourceType> { ResourceType.Iron, ResourceType.Coal, ResourceType.Gold, ResourceType.Copper }.Contains(resType) && new List<PieceType> { PieceType.Ore, PieceType.Crushed }.Contains(piece.PieceType)) {
        return ApplyTextureImpl(mat, originalName, prefabName, ModMogulPlugin.ModMogulPath + "\\OrePieceCreator Defaults\\Whitened_" + resType + "_Ore.png");
      }
      if (new List<ResourceType> { ResourceType.Iron, ResourceType.Coal, ResourceType.Gold, ResourceType.Copper }.Contains(resType) && piece.PieceType == PieceType.OreCluster) {
        return ApplyTextureImpl(mat, originalName, prefabName, ModMogulPlugin.ModMogulPath + "\\OrePieceCreator Defaults\\Whitened_" + resType + "_OreCluster.png");
      }
      if (new List<PieceType> { PieceType.Ingot, PieceType.Plate, PieceType.Rod, PieceType.Gear, PieceType.JunkCast, PieceType.Pipe, PieceType.ThreadedRod }.Contains(piece.PieceType)) {
        return ApplyTextureImpl(mat, originalName, prefabName, ModMogulPlugin.ModMogulPath + "\\OrePieceCreator Defaults\\Whitened_Iron_" + piece.PieceType + "s.png");
      }
      return DummyEnumerator();
    }
    /// <summary>
    /// This is a helper function, created access the only OrePiece's private field _possibleMeshes.<br/>
    /// It is meant for adding alternate Mesh-es to an OrePiece.
    /// </summary>
    /// <param name="piece">OrePiece to interact with.</param>
    /// <param name="meshAction">
    ///   The function that is to be executed on every material of the provided OrePiece.<br/>
    ///    It provides the arguments for the Material to be processed as well as it's Renderer.
    /// </param>
    public static void ModifyPossibleMeshes(this OrePiece piece, Func<Mesh[], Mesh[]> meshAction) {
      var field = typeof(OrePiece).GetField("_possibleMeshes", BindingFlags.NonPublic | BindingFlags.Instance);
      field.SetValue(piece, meshAction((field.GetValue(piece) as Mesh[]) ?? default));
    }
    /// <summary>
    /// This is a helper function, created to iterate over all of an OrePiece's materials.<br/>
    /// Useful when changing color and textures of OrePiece-s.
    /// </summary>
    /// <param name="piece">OrePiece to source the materials from.</param>
    /// <param name="matAction">
    ///   The function that is to be executed on every material of the provided OrePiece.<br/>
    ///    It provides the arguments for the Material to be processed as well as it's Renderer.
    /// </param>
    public static void ForEachMaterial(this OrePiece piece, Action<Renderer, Material> matAction) =>
      piece.gameObject.GetComponentsInChildren<Renderer>().ToList().ForEach((rend) =>
        rend.materials.ToList().ForEach((mat) => matAction(rend, mat)));
    /// <summary>
    /// This function allows to add custom ResourceType recipes to the Casting furnace.
    /// </summary>
    /// <param name="result">The output ResourceType to produce if this recipe is fulfilled.</param>
    /// <param name="ingredients">The ingredients to put into the melting pot of the Casting furnace.</param>
    /// <param name="needsCoal">Wether to ignore, require or disallow the use of the side Coal input of the Casting furnace.</param>
    public static void AddCastingFurnaceResourceTypeRecipe(this ResourceType result,
      List<(float weight, ResourceType type)> ingredients, bool? needsCoal = null) =>
        CastingFurnaceAlloyingPatches.AddResourceTypeRecipe(result, ingredients, needsCoal);
    /// <summary>
    /// This class is used to create new ResourceTypes.
    /// 
    /// First one can use it's constructor to create the ResourceType definition
    ///   and then one can use the CopyPrefabs methods to create OrePiece prefabs.
    /// 
    /// Those then be tinted and re-textured using the `OrePieceGroup.ForEachMaterial`
    ///   method, while providing an Action that contains calls to the methods
    ///   defined in the OrePieceGroup.HeaderT class.
    ///   
    /// Once one is done with creating this new ResourceType's prefabs,
    ///   let this class be deconstructed to finalize adding them to the game.
    /// </summary>
    public class NewResourceTypeDefinition {
      internal static void ReplacePrefabsImpl(List<OrePiece> newPieceList, List<OrePiece> groupPieceList, string originalName, string prefabName, bool meltable, ResourceType orgResTId, ResourceType resourceTypeID) {
        // In order to automatically create casting recipes for new materials,
        //   we need to retrieve the list of casting recipe sets from the Casting Furnace prefab.
        var cFPT = SavingLoadingManager.Instance?.AllSavableObjectPrefabs?.Find((obj) => obj.ToString().Contains("CastingFurnace")).GetComponentInChildren<CastingFurnace>();
        var mRSL = typeof(CastingFurnace).GetField("_moldRecipieSets", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(cFPT) as List<CastingFurnaceMoldRecipieSet>;
        foreach (var piece in groupPieceList) {
          // Generic function used to update prefab variables.
          static void ReplacePrefab(ref OrePiece piece, List<OrePiece> newPieceList, string original, string name) {
            if (piece?.name.Contains(original) ?? false) {
              var tmp = Regex.Replace(piece.name, original, name);
              piece = newPieceList.Find((piece) => piece.name == tmp) ??
                newPieceList.Find((piece) => (piece.name + "_Polished" == tmp) || (piece.name == tmp + "_Polished"));
            }
          }
          foreach (var field in typeof(OrePiece).GetFields()) {
            switch (field.GetValue(piece)) {
              // Used for the "PossibleSievedPrefabs" & "PossibleClusterBreakerPrefabs" fields.
              case List<WeightedOreChance> wocList:
                foreach (var woc in wocList) {
                  ReplacePrefab(ref woc.OrePrefab, newPieceList, originalName, prefabName);
                }
                break;
              // Used for the many PieceType-specific prefab fields.
              case GameObject prefab:
                var pPiece = prefab.GetComponent<OrePiece>();
                //Debug.Log(field + " : " + pPiece);
                if (pPiece == null) break;
                ReplacePrefab(ref pPiece, newPieceList, originalName, prefabName);
                field.SetValue(piece, pPiece?.gameObject);
                break;
            }
          }
          // If the new material is supposed to be meltable, add casting recipes to match.
          if (meltable) foreach (var recipeSet in mRSL) {
            var ironRecipe = recipeSet.Recipies.Find((recipe) => recipe.InputResourceType == orgResTId);
            var newRecipe = new CastingFurnaceRecipie {
              InputResourceType = resourceTypeID,
              OutputPrefab = ironRecipe.OutputPrefab,
              SecondaryOutputPrefab = ironRecipe.SecondaryOutputPrefab
            };
            ReplacePrefab(ref newRecipe.OutputPrefab, newPieceList, originalName, prefabName);
            ReplacePrefab(ref newRecipe.SecondaryOutputPrefab, newPieceList, originalName, prefabName);
            if (newRecipe.OutputPrefab != null) recipeSet.Recipies.Add(newRecipe);
          }
        }
      }
      public class OrePieceGroup(OrePieceGroup.HeaderT header, List<OrePiece> newPieces) {
        public readonly struct HeaderT(ResourceType orgResTypeId, ResourceType resourceType, string originalName, string prefabName, string prettyName, bool meltable) {
          public readonly ResourceType OrgResTypeId = orgResTypeId, ResourceTypeID = resourceType;
          public readonly string OriginalName = originalName, PrefabName = prefabName, PrettyName = prettyName;
          public readonly bool Meltable = meltable;
          public void ApplyTexture(Material mat, string path) =>
            Utility.CoroutineExecutor.StartCoroutine(ApplyTextureImpl(mat, OriginalName, PrettyName, path));
          public void ApplyDefaultTexture(OrePiece piece, Material mat) =>
            Utility.CoroutineExecutor.StartCoroutine(ApplyDefaultTextureImpl(piece, mat, OriginalName, PrettyName));
          public void ApplyTintColor(Material mat, Color color) {
            if (mat.HasProperty("_Color")) mat.color = color;
          }
        }
        public readonly HeaderT Header = header;
        public readonly List<OrePiece> NewPieces = newPieces;
        public void ForEach(Action<HeaderT, OrePiece> matAction) =>
          NewPieces.ForEach((piece) => matAction(Header, piece));
      }
      private static List<WeakReference<NewResourceTypeDefinition>> _runningInstances = [];
      public readonly List<OrePieceGroup> AddedPieceGroups = [];
      public readonly ResourceType ResourceTypeID;
      public readonly string PrettyName;
      public readonly string PrefabName;
      public readonly string InternalName;
      public readonly Color PrintColor;
      public readonly bool Meltable;
      private volatile bool _isFinalized = false;
      /// <summary>
      /// Add a ResourceType definition to the game.
      ///   This is only supposed to be used during 
      /// </summary>
      /// <param name="name">The name of the new ResourceType.</param>
      /// <param name="meltable">Wether or not this new ResourceType is obtainable from a furnace or not.</param>
      /// <param name="color">The associated color with this ResourceType.<br/>
      ///   This is the color that get's used when an item with this ResourceType
      ///   will be printed in the game on any screen.
      /// </param>
      public NewResourceTypeDefinition(string name, bool meltable, Color color) {
        // If this is called after "OnPieceAndResourceTypeCreation", refuse to add anything &
        //   throw an error to inform the modder of breaking enforced rules of use of this API.
        if (_initialized) throw new Exception("The constructor of the 'NewResourceTypeDefinition' class can't be called after the 'OnPieceAndResourceTypeCreation' was finished!\nPlease forward this to the mod author of the " + Assembly.GetCallingAssembly().GetName() + " assembly, to change their code so it doesn't violate assumptions set by the API.");
        // If the ResourceType already exists, refuse to add it again & set the "ResourceType" field to "INVALID".
        if (AllResourceTypes.Find(name) != ResourceType.INVALID) {
          ResourceTypeID = ResourceType.INVALID;
          return;
        }
        ;
        ResourceTypeID = (ResourceType)AllResourceTypes._Types.Count;
        AllResourceTypes[ResourceTypeID] = name;
        MeltableMask[ResourceTypeID] = meltable;
        var desc = new ResourceDescription {
          ResourceType = ResourceTypeID,
          DisplayColor = color
        };
        _addedResourceDescriptions.Add(desc);
        var tmp = AllResourceTypes._Types[ResourceTypeID];
        PrettyName = tmp.PrettyName;
        PrefabName = Regex.Replace(PrettyName, "\\s", "");
        InternalName = tmp.InternalName;
        PrintColor = color;
        Meltable = meltable;
      }
      /// <summary>
      /// Copy all of the prefabs and recipes from some original type.
      ///   It will prevent from adding duplicate types
      ///   (according to "NameEntry.ConvertToInternalName").
      /// </summary>
      /// <param name="originalName">The original ResourceType to copy the selected prefabs from.</param>
      /// <param name="color">The color that will be used to tint the created prefabs. If null it won't tint them.</param>
      /// <param name="basePriceMult">The multiplier to apply to the base prices of generated OrePieces.</param>
      /// <returns>A list of all added OrePieces. Useful if you want to change the
      ///   OrePiece prefabs <i>even more</i>. If there is already a ResourceType that
      ///   matches the internalName of this one, it will return an empty list instead.
      /// </returns>
      public OrePieceGroup CopyPrefabs(string originalName, float basePriceMult = 1.0f)
        => CopyPrefabs(originalName, (_) => true, basePriceMult);
      /// <summary>
      /// Copy some of the prefabs and recipes from some original type.
      ///   It will prevent from adding duplicate types
      ///   (according to "NameEntry.ConvertToInternalName").
      /// </summary>
      /// <param name="originalName">The original ResourceType to copy the selected prefabs from.</param>
      /// <param name="addFilter">The filtering predicate for restricting which orePieces to copy from the original.</param>
      /// <param name="basePriceMult">The multiplier to apply to the base prices of generated OrePieces.</param>
      /// <returns>A list of all added OrePieces. Useful if you want to change the
      ///   OrePiece prefabs <i>even more</i>. If there is already a ResourceType that
      ///   matches the internalName of this one, it will return an empty list instead.
      /// </returns>
      public OrePieceGroup CopyPrefabs(string originalName, Predicate<OrePiece> addFilter, float basePriceMult = 1.0f) {
        // If this is called after it was finalized, refuse to add anything & return null.
        if (_isFinalized) return null;
        // If this is called after "OnPieceAndResourceTypeCreation", refuse to add anything &
        //   throw an error to inform the modder of breaking enforced rules of use of this API.
        if (_initialized) throw new Exception("The method 'NewResourceTypeDefinition.CopyPrefabs' can't be called after the 'OnPieceAndResourceTypeCreation' was finished!\nPlease forward this to the mod author of the " + Assembly.GetCallingAssembly().GetName() + " assembly, to change their code so it doesn't violate assumptions set by the API.");
        // If the "ResourceType" field is set to "INVALID", refuse to do anything & return null.
        if (ResourceTypeID == ResourceType.INVALID) return null;
        _runningInstances.Add(new(this));
        var OrgResTId = AllResourceTypes.Find(originalName); // The original ResourceType's Id.
        OrePieceGroup.HeaderT header = new(OrgResTId, ResourceTypeID, originalName, PrefabName, PrettyName, Meltable);
        var pieceList = SavingLoadingManager.Instance.AllOrePiecePrefabs.FindAll((piece) =>
          piece.ResourceType == OrgResTId).FindAll(addFilter);
        var newPieceList = new List<OrePiece>();
        foreach (var piece in pieceList) { // Create all of the new prefabs.
          var newPiece = GameObject.Instantiate(piece);
          newPiece.gameObject.name = Regex.Replace(piece.gameObject.name, originalName, PrefabName);
          newPiece.name = Regex.Replace(piece.name, originalName, PrefabName);
          newPiece.BaseSellValue = piece.BaseSellValue * basePriceMult;
          newPiece.RandomPriceMultiplier = 1f;
          newPiece.ResourceType = ResourceTypeID;
          // In order for the prefabs to not disappear, we need to parent them
          //   to some GameObject that's marked with "DontDestroyOnLoad"
          newPiece.transform.parent = PrefabHolder.transform;
          newPieceList.Add(newPiece);
        }
        OrePieceGroup group = new(header, newPieceList);
        // Add the added prefabs to a list to be added later to the list of all OrePiece prefabs.
        AddedPieceGroups.Add(group);
        // Return the list of added OrePieces for potential further modifications.
        return group;
      }
      public void FinalizeCreation() {
        if (_isFinalized) return;
        _isFinalized = true;
        List<OrePiece> AddedPieces = [];
        AddedPieceGroups.ForEach((g) => AddedPieces.AddRange(g.NewPieces));
        AddedPieceGroups.ForEach((g) => ReplacePrefabsImpl(AddedPieces, g.NewPieces, g.Header.OriginalName,
          g.Header.PrefabName, g.Header.Meltable, g.Header.OrgResTypeId, g.Header.ResourceTypeID));
        // Finally, add the modified prefabs to the list of all OrePiece prefabs,
        //   so the game can load resources of this type.
        SavingLoadingManager.Instance.AllOrePiecePrefabs.AddRange(AddedPieces);
      }
      ~NewResourceTypeDefinition() {
        FinalizeCreation();
      }
      public static void Cleanup() {
        _runningInstances.ForEach((e) => {
          if (e.TryGetTarget(out var NRTD)) NRTD.FinalizeCreation();
        });
        _runningInstances = [];
      }
    }
    /// <summary>
    /// This class is used to create new PieceTypes.
    /// 
    /// First one can use it's constructor to create the PieceType definition
    ///   and then one can use the CopyPrefabs methods to create OrePiece prefabs.
    /// 
    /// Those then be tinted and re-textured using the `OrePieceGroup.ForEachMaterial`
    ///   method, while providing an Action that contains calls to the methods
    ///   defined in the OrePieceGroup.HeaderT class.
    ///   
    /// Once one is done with creating this new PieceType's prefabs,
    ///   let this class be deconstructed to finalize adding them to the game.
    /// </summary>
    public class NewPieceTypeDefinition {
      public class OrePieceGroup(OrePieceGroup.HeaderT header, List<OrePiece> newPieces) {
        public readonly struct HeaderT(PieceType orgPieceTypeId, PieceType pieceType, string originalName, string prefabName, string prettyName) {
          public readonly PieceType OrgResTypeId = orgPieceTypeId, PieceTypeID = pieceType;
          public readonly string OriginalName = originalName, PrefabName = prefabName, PrettyName = prettyName;
          public void ApplyTexture(Material mat, string path) =>
            Utility.CoroutineExecutor.StartCoroutine(ApplyTextureImpl(mat, OriginalName, PrettyName, path));
          public void ApplyDefaultTexture(OrePiece piece, Material mat) =>
            Utility.CoroutineExecutor.StartCoroutine(ApplyDefaultTextureImpl(piece, mat, OriginalName, PrettyName));
          public void ApplyTintColor(Material mat, Color color) {
            if (mat.HasProperty("_Color")) mat.color = color;
          }
        }
        public readonly HeaderT Header = header;
        public readonly List<OrePiece> NewPieces = newPieces;
        public void ForEach(Action<HeaderT, OrePiece> matAction) =>
          NewPieces.ForEach((piece) => matAction(Header, piece));
      }
      private static List<WeakReference<NewPieceTypeDefinition>> _runningInstances = [];
      public readonly List<OrePieceGroup> AddedPieceGroups = [];
      public readonly PieceType PieceTypeID;
      public readonly string PrettyName;
      public readonly string PrefabName;
      public readonly string InternalName;
      private bool _isFinalized = false;
      /// <summary>
      /// Add a PieceType definition to the game.
      ///   This is only supposed to be used during 
      /// </summary>
      /// <param name="name">The name of the new PieceType.</param>
      /// </param>
      public NewPieceTypeDefinition(string name) {
        // If this is called after "OnPieceAndResourceTypeCreation", refuse to add anything &
        //   throw an error to inform the modder of breaking enforced rules of use of this API.
        if (_initialized) throw new Exception("The constructor of the 'NewPieceTypeDefinition' class can't be called after the 'OnPieceAndResourceTypeCreation' was finished!\nPlease forward this to the mod author of the " + Assembly.GetCallingAssembly().GetName() + " assembly, to change their code so it doesn't violate assumptions set by the API.");
        // If the PieceType already exists, refuse to add it again & set the "PieceType" field to "INVALID".
        if (AllPieceTypes.Find(name) != PieceType.INVALID) {
          PieceTypeID = PieceType.INVALID;
          return;
        };
        PieceTypeID = (PieceType)AllPieceTypes._Types.Count;
        AllPieceTypes[PieceTypeID] = name;
        var tmp = AllPieceTypes._Types[PieceTypeID];
        PrettyName = tmp.PrettyName;
        PrefabName = Regex.Replace(PrettyName, "\\s", "");
        InternalName = tmp.InternalName;
      }
      /// <summary>
      /// Copy all of the prefabs and recipes from some original type.
      ///   It will prevent from adding duplicate types
      ///   (according to "NameEntry.ConvertToInternalName").
      /// </summary>
      /// <param name="originalName">The original PieceType to copy the selected prefabs from.</param>
      /// <param name="basePriceMult">The multiplier to apply to the base prices of generated OrePieces.</param>
      /// <returns>A list of all added OrePieces. Useful if you want to change the
      ///   OrePiece prefabs <i>even more</i>. If there is already a PieceType that
      ///   matches the internalName of this one, it will return an empty list instead.
      /// </returns>
      public OrePieceGroup CopyPrefabs(string originalName, float basePriceMult = 1.0f)
        => CopyPrefabs(originalName, (_) => true, basePriceMult);
      /// <summary>
      /// Copy some of the prefabs and recipes from some original type.
      ///   It will prevent from adding duplicate types
      ///   (according to "NameEntry.ConvertToInternalName").
      /// </summary>
      /// <param name="originalName">The original PieceType to copy the selected prefabs from.</param>
      /// <param name="addFilter">The filtering predicate for restricting which orePieces to copy from the original.</param>
      /// <param name="basePriceMult">The multiplier to apply to the base prices of generated OrePieces.</param>
      /// <returns>A list of all added OrePieces. Useful if you want to change the
      ///   OrePiece prefabs <i>even more</i>. If there is already a PieceType that
      ///   matches the internalName of this one, it will return an empty list instead.
      /// </returns>
      public OrePieceGroup CopyPrefabs(string originalName, Predicate<OrePiece> addFilter, float basePriceMult = 1.0f) {
        // If this is called after it was finalized, refuse to add anything & return null.
        if (_isFinalized) return null;
        // If this is called after "OnPieceAndPieceTypeCreation", refuse to add anything &
        //   throw an error to inform the modder of breaking enforced rules of use of this API.
        if (_initialized) throw new Exception("The method 'NewPieceTypeDefinition.CopyPrefabs' can't be called after the 'OnPieceAndPieceTypeCreation' was finished!\nPlease forward this to the mod author of the " + Assembly.GetCallingAssembly().GetName() + " assembly, to change their code so it doesn't violate assumptions set by the API.");
        // If the "PieceType" field is set to "INVALID", refuse to do anything & return null.
        if (PieceTypeID == PieceType.INVALID) return null;
        var OrgResTId = AllPieceTypes.Find(originalName); // The original PieceType's Id.
        OrePieceGroup.HeaderT header = new(OrgResTId, PieceTypeID, originalName, PrefabName, PrettyName);
        var pieceList = SavingLoadingManager.Instance.AllOrePiecePrefabs.FindAll((piece) =>
          piece.PieceType == OrgResTId).FindAll(addFilter);
        var newPieceList = new List<OrePiece>();
        foreach (var piece in pieceList) { // Create all of the new prefabs.
          var newPiece = GameObject.Instantiate(piece);
          newPiece.gameObject.name = Regex.Replace(piece.gameObject.name, originalName, PrefabName);
          newPiece.name = Regex.Replace(piece.name, originalName, PrefabName);
          newPiece.BaseSellValue = piece.BaseSellValue * basePriceMult;
          newPiece.RandomPriceMultiplier = 1f;
          newPiece.PieceType = PieceTypeID;
          // In order for the prefabs to not disappear, we need to parent them
          //   to some GameObject that's marked with "DontDestroyOnLoad"
          newPiece.transform.parent = PrefabHolder.transform;
          newPieceList.Add(newPiece);
        }
        OrePieceGroup group = new(header, newPieceList);
        // Add the added prefabs to a list to be added later to the list of all OrePiece prefabs.
        AddedPieceGroups.Add(group);
        // Return the list of added OrePieces for potential further modifications.
        return group;
      }
      public void FinalizeCreation() {
        if (_isFinalized) return;
        _isFinalized = true;
        List<OrePiece> AddedPieces = [];
        AddedPieceGroups.ForEach((g) => AddedPieces.AddRange(g.NewPieces));
        // Finally, add the modified prefabs to the list of all OrePiece prefabs,
        //   so the game can load resources of this type.
        SavingLoadingManager.Instance.AllOrePiecePrefabs.AddRange(AddedPieces);
      }
      ~NewPieceTypeDefinition() {
        FinalizeCreation();
      }
      public static void Cleanup() {
        _runningInstances.ForEach((e) => {
          if (e.TryGetTarget(out var NPTD)) NPTD.FinalizeCreation();
        });
        _runningInstances = [];
      }
    }
    [HarmonyPatch(typeof(SavingLoadingManager), "Awake")]
    static class SavingLoadingManager_Awake_Prefix {
      /// <summary>
      /// This is the patch that is used to add custom ResourceTypes to the game.
      ///   It has to be done at this time in particular, because we need the
      ///   SavingLoadingManager to initialize it's "_orePieceLookup" field,
      ///   before it tries to load any OrePieces.
      /// </summary>
      static void Prefix() {
        if (!_initialized) {
          OnPieceAndResourceTypeCreation(PrefabHolder);
          _initialized = true;
          AfterPieceAndResourceTypeCreation();
          CastingFurnaceAlloyingPatches.EvaluatePatching();
          // Add the prefab for our PieceAndResourceTypeDictionary loader.
          var PaRTD = new GameObject("OrePieceCreator_PieceAndResourceTypeDictionary");
          PaRTD.AddComponent<ResourceAndPieceTypeDictionary>().name = "OrePieceCreator_PieceAndResourceTypeDictionary";
          PaRTD.transform.parent = PrefabHolder.transform;
          SavingLoadingManager.Instance.AllSavableObjectPrefabs.Add(PaRTD);
        }
      }
    }
    [HarmonyPatch(typeof(SavingLoadingManager), nameof(SavingLoadingManager.LoadGame))]
    static class SavingLoadingManager_LoadGame_Transpiler {
      /// <summary>
      /// This function creates a ResourceAndPieceTypeDictionary instance,
      ///   if there wasn't one loaded from the savefile.
      /// </summary>
      static void ConditionallyInjectNewPaRTDObject() {
        var PaRTDp = PrefabHolder.GetComponentInChildren<ResourceAndPieceTypeDictionary>();
        var PaRTD = GameObject.FindObjectsByType<ResourceAndPieceTypeDictionary>(FindObjectsSortMode.None).ToList().Find((obj) => obj != PaRTDp)?.gameObject ?? UnityEngine.Object.Instantiate(PaRTDp.gameObject);
        PaRTD.name = PaRTDp.gameObject.name + "_1";
        PaRTD.GetComponent<ResourceAndPieceTypeDictionary>().name = PaRTDp.name + "_1";
      }
      /// <summary>
      /// This function performs the necessary re-mapping of all present ore-pieces,
      ///   right before each one is being loaded. This is required if the Ids of the
      ///   PieceTypes or ResourceTypes don't match between the savefile & what was
      ///   created during the execution of the "OnPieceAndResourceTypeCreation" event.
      /// </summary>
      /// <param name="piece"></param>
      static void RemapExistingOrePieces(OrePieceEntry piece) {
        bool success = ResourceAndPieceTypeDictionary.PieceTypeMap.TryGetValue(piece.PieceType, out PieceType pieceType);
        piece.PieceType = success ? pieceType : piece.PieceType;
        success = ResourceAndPieceTypeDictionary.ResourceTypeMap.TryGetValue(piece.ResourceType, out ResourceType resourceType);
        piece.ResourceType = success ? resourceType : piece.ResourceType;
      }
      static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var newInstructions = instructions.ToList();
        // This needs to be injected before any OrePiece prefabs get added & needs to be called only once.
        var idx = newInstructions.FindIndex((instr) => (instr.opcode == OpCodes.Ldfld) && (instr.operand as FieldInfo == typeof(SaveFile).GetField("OrePieces")));
        newInstructions.InsertRange(idx - 1, [
          CodeInstruction.Call(typeof(SavingLoadingManager_LoadGame_Transpiler), "ConditionallyInjectNewPaRTDObject", [])
        ]);
        // This on the other hand needs to be done for every OrePiece at the very beginning of the Enumerator loop over all of the loaded OrePieces.
        idx = newInstructions.FindIndex((instr) => (instr.opcode == OpCodes.Call) && (instr.operand as MethodInfo == typeof(List<OrePieceEntry>.Enumerator).GetMethod("get_Current")));
        newInstructions.InsertRange(idx + 1, [
          new CodeInstruction(OpCodes.Dup),
          CodeInstruction.Call(typeof(SavingLoadingManager_LoadGame_Transpiler), "RemapExistingOrePieces", [typeof(OrePieceEntry)])
        ]);
        return newInstructions;
      }
    }
    [HarmonyPatch(typeof(SavingLoadingManager), "LoadSceneForNewGame")]
    static class SavingLoadingManager_LoadSceneForNewGame_Postfix {
      /// <summary>
      /// This patch creates a ResourceAndPieceTypeDictionary instance,
      ///   after a new game was started, so the PieceType & ResourceType
      ///   mapping data can be successfully saved later.
      /// </summary>
      static IEnumerator Postfix(IEnumerator __result) {
        while (__result.MoveNext())
          yield return __result.Current;

        var RTDp = PrefabHolder.GetComponentInChildren<ResourceAndPieceTypeDictionary>();
        var RTD = UnityEngine.Object.Instantiate(RTDp.gameObject);
        RTD.name = RTDp.gameObject.name + "_1";
        RTD.GetComponent<ResourceAndPieceTypeDictionary>().name = RTDp.name + "_1";
      }
    }
    [HarmonyPatch(typeof(OreManager), nameof(OreManager.GetResourceColor))]
    static class OreManager_GetResourceColor_Prefix {
      static OreManager lastPatchedOM;
      /// <summary>
      /// This is the current workaround for the OreManager not being loaded,
      ///   when ResourceTypes get created. TODO: find a better spot for this
      ///   to be done, because this is not ideal for performance.
      /// </summary>
      /// <param name="__instance">The OreManager instence to check & potentially update.</param>
      /// <param name="____allResourceDescriptions">The private field that (potentially) needs to be updated.</param>
      static void Prefix(OreManager __instance, List<ResourceDescription> ____allResourceDescriptions) {
        if (__instance != lastPatchedOM) {
          lastPatchedOM = __instance;
          ____allResourceDescriptions.AddRange(_addedResourceDescriptions);
        }
      }
    }
    [HarmonyPatch(typeof(OreManager), nameof(OreManager.GetColoredResourceTypeString))]
    static class OreManager_GetColoredResourceTypeString_Prefix {
      /// <summary>
      /// This needs to be done so the furnaces can print resource text,
      ///   for the new ResourceTypes correctly.
      /// </summary>
      static bool Prefix(ref string __result, OreManager __instance, ResourceType resourceType) {
        string text = __instance.GetResourceColor(resourceType).ToHexString();
        __result = string.Format("<color=#{0}>{1}</color>", text, AllResourceTypes[resourceType]);
        return false;
      }
    }
    [HarmonyPatch(typeof(OreManager), nameof(OreManager.GetColoredFormattedResourcePieceString))]
    static class OreManager_GetColoredFormattedResourcePieceString_Transpiler {
      /// <summary>
      /// This patch is responsible for fixing the printing of custom PieceTypes &
      ///   ResourceTypes, for the Resource Scanner, the Packaging Machine & packaged boxes.
      /// 
      /// This replaces the calls to "PieceType.ToString" "ResourceType.ToString" with a call
      ///   to retrieve the name in our "AllPieceTypes" & "AllResourceTypes" lists of types
      ///   that actually contains the new PieceType & ResourceType names.
      /// </summary>
      static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var newInstructions = instructions.ToList();
        var idx = newInstructions.FindIndex((instr) => (instr.opcode == OpCodes.Constrained) && (instr.operand as Type == typeof(PieceType)));
        newInstructions[idx - 1] = CodeInstruction.LoadField(typeof(OrePieceCreator), "AllPieceTypes");
        newInstructions[idx] = new CodeInstruction(OpCodes.Ldarg_2);
        newInstructions[idx + 1] = CodeInstruction.Call(typeof(AllPieceTypesT), "get_Item");
        idx = newInstructions.FindIndex((instr) => (instr.opcode == OpCodes.Constrained) && (instr.operand as Type == typeof(ResourceType)));
        newInstructions[idx - 1] = CodeInstruction.LoadField(typeof(OrePieceCreator), "AllResourceTypes");
        newInstructions[idx] = new CodeInstruction(OpCodes.Ldarg_1);
        newInstructions[idx + 1] = CodeInstruction.Call(typeof(AllResourceTypesT), "get_Item");
        return newInstructions;
      }
    }
    /*[HarmonyPatch]
    static class ResourceType_ToString_Transpiler {
      static MethodBase TargetMethod() {
        var asm = AccessTools.AllAssemblies().Where((asm) => asm.GetType(nameof(ResourceType)) == typeof(ResourceType)).Single();
        return AccessTools.GetTypesFromAssembly(asm).SelectMany((t) => t.GetMethods()).Where((m) => m.ReflectedType.Namespace == null && m.ReflectedType == typeof(PieceType) && m.Name == "ToString" && m.GetParameters().Count() == 0).First();
      }
      /// <summary>
      /// This patch is responsible for fixing the printing of custom PieceTypes &
      ///   ResourceTypes, for the Resource Scanner, the Packaging Machine & packaged boxes.
      /// 
      /// This replaces the calls to "PieceType.ToString" "ResourceType.ToString" with a call
      ///   to retrieve the name in our "AllPieceTypes" & "AllResourceTypes" lists of types
      ///   that actually contains the new PieceType & ResourceType names.
      /// </summary>
      static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction>_) {
        Utils.DumpLI("", _);
        var instructions = new List<CodeInstruction>(3) {
          CodeInstruction.LoadField(typeof(OrePieceCreator), "AllResourceTypes"),
          new(OpCodes.Ldarg_0),
          CodeInstruction.Call(typeof(Enum), "GetValue"),
          new(OpCodes.Unbox, typeof(ResourceType)),
          CodeInstruction.Call(typeof(AllResourceTypesT), "get_Item"),
          new(OpCodes.Ret)
        };
        Utils.DumpLI("", instructions);
        return instructions;
      }
    }*/
    /*[HarmonyPatch]
    static class GlobalMethod_ToString_Transpiler {
      static IEnumerable<MethodBase> TargetMethods() {
        var asm = AccessTools.AllAssemblies().Where((asm) => asm.GetType(nameof(ResourceType)) == typeof(ResourceType)).Single();
        var ms = AccessTools.GetTypesFromAssembly(asm).SelectMany((t) => t.GetMethods()).Where((m) => m.ReflectedType.Namespace == null && m.ReflectedType == typeof(PieceType) && m.Name == "ToString");
        foreach (var m in ms) {
          Debug.Log("Patching: " + m.ReturnType + " " + m.ReflectedType.Namespace + ":" + m.ReflectedType + "." + m.Name + "(" + string.Concat(m.GetParameters().Select((p) => ", " + p.ParameterType + " " + p.Name)).Substring(2) + ")");
          yield return m;
        }
        yield break;
      }
      /// <summary>
      /// This patch is responsible for fixing the printing of custom PieceTypes &
      ///   ResourceTypes, for the Resource Scanner, the Packaging Machine & packaged boxes.
      /// 
      /// This replaces the calls to "PieceType.ToString" "ResourceType.ToString" with a call
      ///   to retrieve the name in our "AllPieceTypes" & "AllResourceTypes" lists of types
      ///   that actually contains the new PieceType & ResourceType names.
      /// </summary>
      private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) {
        Utils.DumpLI("Patching: " + ((__originalMethod != null) ? __originalMethod.ToString() : null), instructions);
        try {
          var newInstructions = instructions.ToList();
          for (var idx = 0; idx < newInstructions.Count; idx++) {
            CodeInstruction codeInstruction = newInstructions[idx];
            if (codeInstruction.opcode == OpCodes.Constrained) {
              if (codeInstruction.operand as Type == typeof(PieceType)) {
                newInstructions[idx - 1] = CodeInstruction.LoadField(typeof(OrePieceCreator), "AllPieceTypes", false);
                newInstructions[idx] = new CodeInstruction(OpCodes.Ldarg_2, null);
                newInstructions[idx + 1] = CodeInstruction.Call(typeof(OrePieceCreator.AllPieceTypesT), "get_Item", null, null);
                continue;
              } 
              if (codeInstruction.operand as Type == typeof(ResourceType)) {
                newInstructions[idx - 1] = CodeInstruction.LoadField(typeof(OrePieceCreator), "AllResourceTypes", false);
                newInstructions[idx] = new CodeInstruction(OpCodes.Ldarg_1, null);
                newInstructions[idx + 1] = CodeInstruction.Call(typeof(OrePieceCreator.AllResourceTypesT), "get_Item", null, null);
                continue;
              }
            }
          }
          return newInstructions;
        } catch {
          return instructions;
        }
      }
    }*/
    [HarmonyPatch(typeof(BoxObject), nameof(BoxObject.LoadFromSave))]
    static class BoxObject_LoadFromSave_Transpiler {
      static void CheckBoxContents(BoxContents bC) {
        bC.Contents.RemoveAll((bCE) =>
          (!AllPieceTypes._Types.ContainsKey(bCE.PieceType)) ||
          (!AllResourceTypes._Types.ContainsKey(bCE.ResourceType)));
      }
      /// <summary>
      /// This patch fixes the crash when one loads a savefile that contains
      ///   packaged boxes, that contain OrePieces with illegal PiceTypes or ResourceTypes.
      ///   I think this should really be a part of the base game though.
      ///   TODO: Convince the game devs to add this functionality to the base game.
      /// </summary>
      static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var newInstructions = instructions.ToList();
        var idx = newInstructions.FindIndex((instr) => (instr.opcode == OpCodes.Call) && (instr.operand as MethodInfo == typeof(BoxObject).GetMethod("Initialize", [typeof(BoxContents)])));
        newInstructions.InsertRange(idx, [
          new CodeInstruction(OpCodes.Dup),
          CodeInstruction.Call(typeof(BoxObject_LoadFromSave_Transpiler), "CheckBoxContents")
        ]);
        return newInstructions;
      }
    }
    [HarmonyPatch(typeof(PackagerMachine), nameof(PackagerMachine.LoadFromSave))]
    static class PackagerMachine_LoadFromSave_Transpiler {
      /// <summary>
      /// This patch fixes the crash when one loads a savefile,
      ///   that contains partially filled ones stuck in packagers,
      ///   that contain OrePieces with illegal PiceTypes or ResourceTypes.
      ///   I think this should really be a part of the base game though.
      ///   TODO: Convince the game devs to add this functionality to the base game.
      /// </summary>
      static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var newInstructions = instructions.ToList();
        var idx = newInstructions.FindIndex((instr) => (instr.opcode == OpCodes.Stfld) && (instr.operand as FieldInfo == typeof(PackagerMachine).GetField("CurrentBoxContents")));
        newInstructions.InsertRange(idx, [
          new CodeInstruction(OpCodes.Dup),
          CodeInstruction.Call(typeof(BoxObject_LoadFromSave_Transpiler), "CheckBoxContents")
        ]);
        return newInstructions;
      }
    }
    [HarmonyPatch(typeof(BlastFurnace), "UpdateProjectedOutputResource")]
    static class BlastFurnace_UpdateProjectedOutputResource_Transpiler {
      /// <summary>
      /// In order to patch the behavior of the screen on the Blast Furnace,
      ///   such that it supports custom ResourceTypes, we need to patch this
      ///   so it respects the "meltabe"-ility of the constructed resource types.
      /// In the case that a resource can't be melted into an ingot (because
      ///   it was specified as such during the ResourceType's creation) the
      ///   furnace should output "Slag" instead, so the screen needs to
      ///   reflect that (as it does in the base game).
      /// </summary>
      static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var newInstructions = instructions.ToList();
        var sIdx = newInstructions.FindIndex((instr) => (instr.opcode == OpCodes.Call) && (instr.operand as MethodInfo == typeof(BlastFurnace).GetMethod("DetermineOutputResourceType", BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(List<ResourceType>)], null))) + 2; // + 2 because we want to keep this & the succeeding 'stloc.0' instructions
        var eIdx = newInstructions.FindLastIndex((instr) => (instr.opcode == OpCodes.Ldfld) && (instr.operand as FieldInfo == typeof(BlastFurnace).GetField("_outputProductText", BindingFlags.NonPublic | BindingFlags.Instance))) - 4; // - 4 because we don't want to remove those 4 preceding instructions
        Label elseLabel = default, endLabel = default; // since we need to make an if-else statement, we need some labels for jumping purposes,
        foreach (var instruction in newInstructions)   // which we're recovering from the if statements that we no longer need
          foreach (var label in instruction.labels)
            switch (label.GetHashCode()) {
              case 2:
                elseLabel = label; // new label for "the beginning" of the `else` block body
                break;
              case 3:
                endLabel = label;  // new label for "after the end" of the `else` block body
                break;
            }
        newInstructions[eIdx].operand = endLabel; // change the existing jump instruction to point at a different label
        newInstructions[eIdx + 1].labels = [elseLabel]; // re-do labels for the instruction at the beginning of the `else` block body
        newInstructions[eIdx + 3].labels = [endLabel];  // re-do labels for the instruction after the end of the `else` block body
        newInstructions.RemoveRange(sIdx, eIdx - sIdx); // remove all of the excess if statement nonsense
        newInstructions.InsertRange(sIdx, [ // replace it with the "meltable" metadata evaluation
          CodeInstruction.LoadField(typeof(OrePieceCreator), "MeltableMask"),
          new CodeInstruction(OpCodes.Ldloc_0),
          CodeInstruction.Call(typeof(MeltableMaskT), "get_Item"),
          new CodeInstruction(OpCodes.Brfalse_S, elseLabel),
          new CodeInstruction(OpCodes.Ldloc_0),
          new CodeInstruction(OpCodes.Stloc_1)
        ]);
        return newInstructions;
      }
    }
    [HarmonyPatch(typeof(BlastFurnace), "CreateOutputOrePiece")]
    static class BlastFurnace_CreateOutputOrePiece_Prefix {
      static Dictionary<ResourceType, OrePiece> _ingotPrefabs;
      static Dictionary<ResourceType, OrePiece> IngotPrefabs {
        get {
          if (_ingotPrefabs != null) return _ingotPrefabs;
          _ingotPrefabs = SavingLoadingManager.Instance.AllOrePiecePrefabs.FindAll((piece) => (piece.PieceType == PieceType.Ingot) && !piece.IsPolished).ToDictionary((piece) => piece.ResourceType);
          return _ingotPrefabs;
        }
      }
      /// <summary>
      /// This is done to make sure that Blast Furnaces respect,
      ///   and create the ingots of added ResourceTypes, which were
      ///   specified to do so (via the "meltable" parameter).
      /// </summary>
      static bool Prefix(BlastFurnace __instance, ResourceType resourceType) {
        GameObject selectedPrefab = __instance.SlagPrefab;
        if (MeltableMask[resourceType]) selectedPrefab = IngotPrefabs[resourceType].gameObject;
        if (selectedPrefab != null) {
          Singleton<OrePiecePoolManager>.Instance.TrySpawnPooledOre(selectedPrefab, __instance.OutputTransform.position, __instance.OutputTransform.rotation, null);
        }
        return false;
      }
    }
    [HarmonyPatch(typeof(CastingFurnace), "UpdateProjectedOutputResource")]
    static class CastingFurnace_UpdateProjectedOutputResource_Transpiler {
      /// <summary>
      /// In order to patch the behavior of the screen on the Casting Furnace,
      ///   such that it supports custom ResourceTypes, we need to patch this
      ///   so it respects the "meltabe"-ility of the constructed resource types.
      /// In the case that a resource can't be melted into ingots or gears (because
      ///   it was specified as such during the ResourceType's creation) the
      ///   furnace should output "Slag" instead, so the screen needs to
      ///   reflect that (as it does in the base game).
      /// </summary>
      static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var newInstructions = instructions.ToList();
        var sIdx = newInstructions.FindIndex((instr) => (instr.opcode == OpCodes.Call) && (instr.operand as MethodInfo == typeof(CastingFurnace).GetMethod("DetermineOutputResourceType", BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(List<ResourceType>)], null))) + 2; // + 2 because we want to keep this & the succeeding 'stloc.0' instructions
        var eIdx = newInstructions.FindLastIndex((instr) => (instr.opcode == OpCodes.Ldfld) && (instr.operand as FieldInfo == typeof(CastingFurnace).GetField("_outputProductText", BindingFlags.NonPublic | BindingFlags.Instance))) - 4; // - 4 because we don't want to remove those 4 preceding instructions
        Label elseLabel = default, endLabel = default; // since we need to make an if-else statement, we need some labels for jumping purposes,
        foreach (var instruction in newInstructions)   // which we're recovering from the if statements that we no longer need
          foreach (var label in instruction.labels)
            switch (label.GetHashCode()) {
              case 2:
                elseLabel = label; // new label for "the beginning" of the `else` block body
                break;
              case 3:
                endLabel = label;  // new label for "after the end" of the `else` block body
                break;
            }
        newInstructions[eIdx].operand = endLabel; // change the existing jump instruction to point at a different label
        newInstructions[eIdx + 1].labels = [elseLabel]; // re-do labels for the instruction at the beginning of the `else` block body
        newInstructions[eIdx + 3].labels = [endLabel];  // re-do labels for the instruction after the end of the `else` block body
        newInstructions.RemoveRange(sIdx, eIdx - sIdx); // remove all of the excess if statement nonsense
        newInstructions.InsertRange(sIdx, [ // replace it with the "meltable" metadata evaluation
          CodeInstruction.LoadField(typeof(OrePieceCreator), "MeltableMask"),
          new CodeInstruction(OpCodes.Ldloc_0),
          CodeInstruction.Call(typeof(MeltableMaskT), "get_Item"),
          new CodeInstruction(OpCodes.Brfalse_S, elseLabel),
          new CodeInstruction(OpCodes.Ldloc_0),
          new CodeInstruction(OpCodes.Stloc_1)
        ]);
        return newInstructions;
      }
    }
	  [HarmonyPatch(typeof(CastingFurnace), "DetermineOutputResourceType")]
    static class CastingFurnaceAlloyingPatches {
      internal readonly struct ResourceTypeRecipe {
        /// <summary>
        /// The ResourceType to produce when successfully matched.
        /// </summary>
        internal readonly ResourceType Result;
        /// <summary>
        /// Wether to ignore, require or disallow the use of the side Coal input of the Casting furnace.
        /// </summary>
        internal readonly bool? NeedsCoal;
        /// <summary>
        /// The ingredients to put into the melting pot of the Casting furnace.
        /// <br><br>
        /// The weights stored here represent the given ResourceType's percentage
        ///   in the required mix, sorted in descending order by their required percentage,
        ///   excluding the part of the mixture that involves the ResourceTypes that came before them in this list.<br>
        /// This means that the last type will always have a "weight" of '1.0f'.
        /// </summary>
        internal readonly List<(float weight, ResourceType type)> Ingredients;
        internal ResourceTypeRecipe(ResourceType result, List<(float weight, ResourceType type)> ingredients, bool? needsCoal = null) {
          Result = result; NeedsCoal = needsCoal;
          // Ensure that there are no multiple entries per the same ResourceType before sorting by weight
          ingredients = [.. ingredients.GroupBy((i) => i.type).Select((g) => (g.Sum((i) => i.weight), g.Key))];
          ingredients.Sort((a, b) => -a.weight.CompareTo(b.weight));
          float total = ingredients.Sum((i) => i.weight);
          Ingredients = [.. ingredients.Select((i) => {
            var weight = i.weight / total;
            total -= i.weight;
            return (weight, i.type);
          })];
        }
        /// <summary>
        /// Check if a recipe requires the same conditions as the other provided recipe.
        /// </summary>
        /// <param name="other">The other recipe to compare against.</param>
        /// <returns></returns>
        internal bool CompareIngredients(ResourceTypeRecipe other) {
          if ((NeedsCoal != null) && (other.NeedsCoal != null) && (NeedsCoal != other.NeedsCoal)) return false;
          if (Ingredients.Count != other.Ingredients.Count) return false;
          for (int i = 0; i < Ingredients.Count; i++) {
            if (Ingredients[i].type != other.Ingredients[i].type) return false;
            if (!Mathf.Approximately(Ingredients[i].weight, other.Ingredients[i].weight)) return false;
          }
          return true;
        }
        internal List<(ResourceType type, int count)> GetCompArr(int requiredToSmelt) {
          return [.. Ingredients.Select((i) => {
            var cnt = Mathf.RoundToInt(i.weight * requiredToSmelt);
            requiredToSmelt -= cnt;
            return (i.type, cnt);
          })];
        }
        internal void PrepVisColl(ref List<(ResourceType type, int count)> cmpArr, ref List<ResourceType> processingResources, ref List<ResourceType> allResources) {
          processingResources.Clear();
          foreach (var (type, count) in cmpArr.AsEnumerable().Reverse()) {
            processingResources.AddRange(Enumerable.Repeat(type, count));
            int i = 0;
            allResources.RemoveAll((r) => i++ < count);
          }
        }
      }
      internal static List<ResourceTypeRecipe> resourceTypeRecipes = [];
      static bool WasPatched = false;
      static Harmony alloyingPatches;
      static readonly MethodInfo CastingFurnace_Update = AccessTools.DeclaredMethod(typeof(CastingFurnace), "Update");
      /// <summary>
      /// This function allows to add custom ResourceType recipes to the Casting furnace.
      /// </summary>
      /// <param name="result">The output ResourceType to produce if this recipe is fulfilled.</param>
      /// <param name="ingredients">The ingredients to put into the melting pot of the Casting furnace.</param>
      /// <param name="needsCoal">Wether to ignore, require or disallow the use of the side Coal input of the Casting furnace.</param>
      internal static void AddResourceTypeRecipe(ResourceType result, List<(float weight, ResourceType type)> ingredients, bool? needsCoal = null) {
        if (!_initialized) {
          Debug.Log("Cannot add ResourceType creation recipes before the definition of ResourceTypes wasn't finalized!!!\nThis method is only supposed to be called in the 'AfterPieceAndResourceTypeCreation' callback,\nwhere a mod is supposed to fetch the necessary ResourceType IDs.\nSkipping addition of ResourceType creation recipe for '" + AllResourceTypes[result] + "' ...");
          return; 
        }
        if (ingredients.Count < 1) {
          Debug.Log("An ResourceType creation recipe must require at least 1 ingredient (in the melting pot)!!!\nSkipping addition of ResourceType creation recipe for '" + AllResourceTypes[result] + "' ...");
          return; 
        }
        ResourceTypeRecipe newRecipe = new(result, ingredients, needsCoal);
        if (resourceTypeRecipes.Any(newRecipe.CompareIngredients)) {
          Debug.Log("Cannot add multiple ResourceType creation recipes with identical ingredient requirements!!!\nSkipping addition of recipe ResourceType creation for '" + AllResourceTypes[result] + "' ...");
          return;
        }
        resourceTypeRecipes.Add(newRecipe);
      }
      internal static void EvaluatePatching() {
        alloyingPatches ??= new Harmony("modmogul.core.alloying");
        var patchLegacyAlloying = resourceTypeRecipes.Any((r) => r.Ingredients.Count > 1);
        if (WasPatched != patchLegacyAlloying) {
          WasPatched = patchLegacyAlloying;
          if (patchLegacyAlloying) {
            resourceTypeRecipes.Sort((a, b) => a.Ingredients.Count.CompareTo(b.Ingredients.Count));
            alloyingPatches.Patch(CastingFurnace_Update, transpiler: new(typeof(CastingFurnaceAlloyingPatches), "CastingFurnace_Update_Transpiler", [typeof(IEnumerable<CodeInstruction>)]));
          } else {
            alloyingPatches.Unpatch(CastingFurnace_Update, HarmonyPatchType.Transpiler);
          }
        }
      }
      static readonly MethodInfo refreshDisplayMethod = typeof(CastingFurnace).GetMethod("RefreshContentsDisplay", BindingFlags.NonPublic | BindingFlags.Instance);
      static bool Prefix(ref ResourceType __result, CastingFurnace __instance, int ____materialRequiredToSmelt, ref Queue<ResourceType> ___resourceQueue, ref Queue<ResourceType> ___visualResourceQueue, List<ResourceType> processingResources) {
        var allResources = new List<ResourceType>(processingResources);
        var cM = new System.Diagnostics.StackTrace().GetFrame(2).GetMethod();
        var isProcessOreCalling = (cM.Name == "MoveNext" && cM.DeclaringType.Name.Contains("ProcessOre")) || (cM.Name == "ProcessOre");
        if (isProcessOreCalling) allResources.AddRange(___resourceQueue);
        var counts = allResources.GroupBy((r) => r).Select((g) => (g.Key, g.Count())).OrderByDescending((a) => a.Item2).ToList();
        foreach (var recipe in resourceTypeRecipes) {
          if ((recipe.NeedsCoal != null) && (recipe.NeedsCoal != (__instance.CoalAmount > __instance.GetRequiredCoalForSteel()))) continue;
          if (counts.Count < recipe.Ingredients.Count) return true;
          var cmpArr = recipe.GetCompArr(____materialRequiredToSmelt);
          for (int i = 0; i < recipe.Ingredients.Count; i++) {
            if (counts[i].Key != cmpArr[i].type) goto next_recipe;
            if (counts[i].Item2 < cmpArr[i].count) goto next_recipe;
          }
          __result = recipe.Result;
          if (!isProcessOreCalling) return false;
          recipe.PrepVisColl(ref cmpArr, ref processingResources, ref allResources);
          ___resourceQueue = new Queue<ResourceType>(allResources);
          var newVisualList = new List<ResourceType>(processingResources.Take(Math.Min(processingResources.Count, ___visualResourceQueue.Count)));
          newVisualList.AddRange(allResources.Take(Math.Max(___visualResourceQueue.Count - ____materialRequiredToSmelt, 0)));
          ___visualResourceQueue = new Queue<ResourceType>(newVisualList);
          refreshDisplayMethod.Invoke(__instance, null);
          return false;
          next_recipe: continue;
        }
        return true;
      }
      static MethodInfo oreProcessingProxy;
      enum CoroutineState {
        NotRunning, WaitingForQuickContinue, RunningNormally, RunningDelayed
      }
      static Dictionary<CastingFurnace, Coroutine> coroutines = new Dictionary<CastingFurnace, Coroutine>();
      static Dictionary<CastingFurnace, CoroutineState> coroutineState = new Dictionary<CastingFurnace, CoroutineState>();
      static void TranspilerInjection(CastingFurnace cF, Queue<ResourceType> resourceQueue, int _materialRequiredToSmelt) {
        if (!coroutineState.ContainsKey(cF)) {
          coroutineState.Add(cF, default);
          coroutines.Add(cF, null);
        }
        if (coroutineState[cF] == CoroutineState.RunningNormally) return;
        if (resourceQueue.Count >= (_materialRequiredToSmelt << 1)) {
          cF.StopCoroutine(coroutines[cF]);
          coroutines[cF] = cF.StartCoroutine(OreProcessingCoroutineTrigger());
          return;
        }
        if (resourceQueue.Count >= _materialRequiredToSmelt) {
          switch (coroutineState[cF]) {
            case CoroutineState.WaitingForQuickContinue:
              cF.StopCoroutine(coroutines[cF]);
              coroutines[cF] = cF.StartCoroutine(OreProcessingCoroutineTrigger());
              return;
            case CoroutineState.NotRunning:
              coroutines[cF] = cF.StartCoroutine(DelayedOreProcessingCoroutineTrigger());
              return;
          }
        }
        return;
        IEnumerator DelayedOreProcessingCoroutineTrigger() {
          coroutineState[cF] = CoroutineState.RunningDelayed;
          yield return new WaitForSeconds(5);
          yield return OreProcessingCoroutineTrigger();
        }
        IEnumerator OreProcessingCoroutineTrigger() {
          coroutineState[cF] = CoroutineState.RunningNormally;
          yield return oreProcessingProxy.Invoke(cF, null) as IEnumerator;
          coroutineState[cF] = CoroutineState.WaitingForQuickContinue;
          yield return new WaitForSeconds(0.5f);
          coroutineState[cF] = CoroutineState.NotRunning;
        }
      }
      static IEnumerable<CodeInstruction> CastingFurnace_Update_Transpiler(IEnumerable<CodeInstruction> instructions) {
        var newInstructions = instructions.ToList();
        var idx = newInstructions.FindIndex((instr) => (instr.opcode == OpCodes.Call) && (instr.operand as MethodInfo == typeof(MonoBehaviour).GetMethod("StartCoroutine", new Type[] { typeof(IEnumerator) })));
        oreProcessingProxy = newInstructions[idx - 1].operand as MethodInfo;
        newInstructions.RemoveRange(idx - 11, 13); // this is intentionally retaining the ldarg.0 instruction from the beginning of the if statement code.
        newInstructions.InsertRange(idx - 11, [
          new(OpCodes.Ldarg_0),
          CodeInstruction.LoadField(typeof(CastingFurnace), "resourceQueue"),
          new(OpCodes.Ldarg_0),
          CodeInstruction.LoadField(typeof(CastingFurnace), "_materialRequiredToSmelt"),
          CodeInstruction.Call(typeof(CastingFurnaceAlloyingPatches), "TranspilerInjection")
        ]);
        return newInstructions;
      }
    }
  }
}