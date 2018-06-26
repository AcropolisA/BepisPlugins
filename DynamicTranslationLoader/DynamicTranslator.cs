using BepInEx;
using BepInEx.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ADV;
using BepInEx.Logging;
using TARC.Compiler;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Logger = BepInEx.Logger;

namespace DynamicTranslationLoader
{
    [BepInPlugin(GUID: "com.bepis.bepinex.dynamictranslator", Name: "Dynamic Translator", Version: "3.0")]
    public class DynamicTranslator : BaseUnityPlugin
    {
        public static List<Archive> TLArchives = new List<Archive>();

		private static Dictionary<string, CompiledLine> textTranslations = new Dictionary<string, CompiledLine>();

		private static Dictionary<Regex, CompiledLine> regexTranslations = new Dictionary<Regex, CompiledLine>();

        private static Dictionary<WeakReference, string> originalTranslations = new Dictionary<WeakReference, string>();

        private static HashSet<string> untranslated = new HashSet<string>();

        public static event Func<object, string, string> OnUnableToTranslateUGUI;

        public static event Func<object, string, string> OnUnableToTranslateTextMeshPro;


	    private static string CurrentEXE = Process.GetCurrentProcess().ProcessName.Replace(".exe", "");

        Event ReloadTranslationsKeyEvent = Event.KeyboardEvent("f10");
        Event DumpUntranslatedTextKeyEvent = Event.KeyboardEvent("#f10");


        //ITL
        private static string TL_DIR_ROOT = null;
        private static string TL_DIR_SCENE = null;
        private static bool isDumpingEnabled = true;
        private static Dictionary<string, Dictionary<string, byte[]>> textureLoadTargets = new Dictionary<string, Dictionary<string, byte[]>>();
        private static Dictionary<string, HashSet<TextureMetadata>> textureDumpTargets = new Dictionary<string, HashSet<TextureMetadata>>();
        private static Dictionary<string, FileStream> fs_textureNameDump = new Dictionary<string, FileStream>();
        private static Dictionary<string, StreamWriter> sw_textureNameDump = new Dictionary<string, StreamWriter>();
        private static IEqualityComparer<TextureMetadata> tmdc = new TextureMetadataComparer();

        void Awake()
        {
            LoadTranslations();

            Hooks.InstallHooks();

            ResourceRedirector.ResourceRedirector.AssetResolvers.Add(RedirectHook);

            TranslateAll();
        }


        void LoadTranslations()
        {
			Logger.Log(LogLevel.Debug, "Loading all translations");

			//TODO: load .bin files here
			
	        Logger.Log(LogLevel.Debug, $"Loaded {TLArchives.Count} archives");

            TLArchives.Clear();

	        var dirTranslation = Path.Combine(Utility.PluginsDirectory, "translation");
	        var dirTranslationText = Path.Combine(dirTranslation, "Text");
	        if (!Directory.Exists(dirTranslationText))
		        Directory.CreateDirectory(dirTranslationText);

	        try
	        {
		        TLArchives.Add(new MarkupCompiler().CompileArchive(dirTranslationText));

		        Logger.Log(LogLevel.Debug, $"Loaded {TLArchives.Last().Sections.Sum(x => x.Lines.Count)} lines from text");
	        }
			catch (Exception ex)
	        {
				Logger.Log(LogLevel.Error | LogLevel.Message, "Unable to load translations from text!");
				Logger.Log(LogLevel.Error, ex);
	        }
            

            //ITL
            var di_tl = new DirectoryInfo(Path.Combine(dirTranslation, "Images"));

            TL_DIR_ROOT = $"{di_tl.FullName}/{Application.productName}";
            TL_DIR_SCENE = $"{TL_DIR_ROOT}/Scenes";

            isDumpingEnabled = BepInEx.Config.GetEntry(nameof(isDumpingEnabled), "1", nameof(DynamicTranslator)) == "1";

            var di = new DirectoryInfo(TL_DIR_SCENE);
            if (!di.Exists) di.Create();

            foreach (var t in new DirectoryInfo(TL_DIR_ROOT).GetFiles("*.txt"))
            {
                var sceneName = t.Name;
                sceneName = sceneName.Substring(0, sceneName.Length - 4);   //Trim .txt
                textureLoadTargets[sceneName] = new Dictionary<string, byte[]>();
                foreach (var tl in File.ReadAllLines(t.FullName))
                {
                    var tp = tl.Split('=');
                    var path = $"{TL_DIR_SCENE}/{tp[1]}";
                    if (!File.Exists(path)) continue;
                    textureLoadTargets[sceneName][tp[0]] = File.ReadAllBytes(path);
                }
            }

            SceneManager.sceneUnloaded += (s) =>
            {
                if (isDumpingEnabled)
                {
                    var sn = s.name;
                    StreamWriter sw = null;
                    if (!sw_textureNameDump.TryGetValue(sn, out sw)) return;
                    sw.Flush();
                }
            };
        }

        void LevelFinishedLoading(Scene scene, LoadSceneMode mode)
        {
			TranslateScene(scene);
		}

	    private static bool TryGetRegex(string input, out CompiledLine line, out Match regexMatch)
	    {
		    Match match;
		    foreach (var kv in regexTranslations)
		    {
			    if ((match = kv.Key.Match(input)).Success)
			    {
				    line = kv.Value;
				    regexMatch = match;
				    return true;
			    }
		    }

		    line = null;
		    regexMatch = null;
		    return false;
	    }

        public static string Translate(string input, object obj)
        {
            GUIUtility.systemCopyBuffer = input;

            if(string.IsNullOrEmpty(input)) 
	            return input;

	        string trimmedInput = input.Trim();

            // Consider changing this! You have a dictionary, but you iterate instead of making a lookup. Why do you not use the WeakKeyDictionary, you have instead? 
            if (!originalTranslations.Any(x => x.Key.Target == obj)) //check if we don't have the object in the dictionary
            {
                //add to untranslated list
                originalTranslations.Add(new WeakReference(obj), input);
            }

	        CompiledLine translation;
	        if (textTranslations.TryGetValue(trimmedInput, out translation))
            { 
                return translation.TranslatedLine;
            }
			else if (TryGetRegex(input, out translation, out Match match))
	        {
		        return translation.TranslatedLine;
	        }
            else if(obj is Text)
            {
                var immediatelyTranslated = OnUnableToTranslateUGUI?.Invoke( obj, input );
                if( immediatelyTranslated != null ) return immediatelyTranslated;
            }
            else if(obj is TMP_Text)
            {
                var immediatelyTranslated = OnUnableToTranslateTextMeshPro?.Invoke( obj, input );
                if( immediatelyTranslated != null ) return immediatelyTranslated;
            }
            
            // Consider changing this! You make a value lookup in a dictionary, which scales really poorly
            if (!untranslated.Contains(trimmedInput))
                untranslated.Add(trimmedInput);

            return input;
        }

        void TranslateAll()
        {
            foreach (TextMeshProUGUI gameObject in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>())
            {
                //gameObject.text = "Harsh is shit";

                gameObject.text = Translate(gameObject.text, gameObject);
            }
        }

        void UntranslateAll()
        {
            Hooks.TranslationHooksEnabled = false;

            int i = 0;

            foreach (var kv in originalTranslations)
            {
                if (kv.Key.IsAlive)
                {
                    i++;

                    if (kv.Key.Target is TMP_Text)
                    {
                        TMP_Text tmtext = (TMP_Text)kv.Key.Target;

                        tmtext.text = kv.Value;
                    }
                    else if (kv.Key.Target is TextMeshProUGUI)
                    {
                        TextMeshProUGUI tmtext = (TextMeshProUGUI)kv.Key.Target;

                        tmtext.text = kv.Value;
                    }
                    else if (kv.Key.Target is UnityEngine.UI.Text)
                    {
                        UnityEngine.UI.Text tmtext = (UnityEngine.UI.Text)kv.Key.Target;

                        tmtext.text = kv.Value;
                    }
                }
            }

            Logger.Log(LogLevel.Message, $"{i} translations reloaded.");

            Hooks.TranslationHooksEnabled = true;
        }

        void Retranslate()
        {
            UntranslateAll();

            LoadTranslations();

            TranslateAll();
        }

	    void LoadSceneTranslations(int sceneIndex)
	    {
			Logger.Log(LogLevel.Debug, $"Loading translations for scene {sceneIndex}");

		    textTranslations.Clear();
		    regexTranslations.Clear();

		    foreach (Archive arc in TLArchives)
				foreach (Section section in arc.Sections)
				{
					if (!section.Exe.Equals("all", StringComparison.OrdinalIgnoreCase) && !section.Exe.Equals(CurrentEXE, StringComparison.OrdinalIgnoreCase))
						continue;

					foreach (var line in section.Lines)
					{
						if (line.Levels.Any(x => x == (byte)sceneIndex || x == 255))
						{
							if (line.Flags.IsOriginalRegex)
								regexTranslations[new Regex(line.OriginalLine)] = line;
							else
								textTranslations[line.OriginalLine] = line;
						}
					}
				}
	    }

        void TranslateScene(Scene scene)
        {
			LoadSceneTranslations(scene.buildIndex);

	        Logger.Log(LogLevel.Debug, $"Translating scene {scene.buildIndex}");

			foreach (GameObject obj in scene.GetRootGameObjects())
				foreach (TextMeshProUGUI gameObject in obj.GetComponentsInChildren<TextMeshProUGUI>(true))
				{
					//gameObject.text = "Harsh is shit";

					gameObject.text = Translate(gameObject.text, gameObject);
				}
		}

        void Dump()
        {
            string output = "";

            foreach (var text in untranslated)
                if (!Regex.Replace(text, @"[\d-]", string.Empty).IsNullOrWhiteSpace()
                        && !text.Contains("Reset"))
                    output += $"{text.Trim()}=\r\n";

            File.WriteAllText("dumped-tl.txt", output);
        }

        void Update()
        {
            if (Event.current == null) return;
            if (UnityEngine.Event.current.Equals(ReloadTranslationsKeyEvent))
            {
                Retranslate();
                Logger.Log(LogLevel.Message, $"Translation reloaded.");
            }
            if (UnityEngine.Event.current.Equals(DumpUntranslatedTextKeyEvent))
            {
                Dump();
                Logger.Log(LogLevel.Message, $"Text dumped to \"{Path.GetFullPath("dumped-tl.txt")}\"");
            }
        }


        #region ITL
        internal static void PrepDumper(string s)
        {
            if (isDumpingEnabled)
            {
                if (textureDumpTargets.ContainsKey(s)) return;
                textureDumpTargets[s] = new HashSet<TextureMetadata>(tmdc);
                fs_textureNameDump[s] = new FileStream($"{TL_DIR_ROOT}/dump_{s}.txt", FileMode.Create, FileAccess.Write);  //TODO: Sanitise scene name?
                sw_textureNameDump[s] = new StreamWriter(fs_textureNameDump[s]);
            }
        }

        internal static bool IsSwappedTexture(Texture t) => t.name.StartsWith("*");

        internal static void ReplaceTexture(Texture2D t2d, string path, string s)
        {
            if (t2d == null) return;
            if (!textureLoadTargets.ContainsKey(s)) return;
            if (!textureLoadTargets[s].ContainsKey(t2d.name)) return;    //TODO: Hash?
            var tex = textureLoadTargets[s][t2d.name];
            if (IsSwappedTexture(t2d)) return;
            t2d.LoadImage(tex);
            t2d.name = "*" + t2d.name;
        }

        internal static void ReplaceTexture(Material mat, string path, string s)
        {
            if (mat == null) return;
            if (!textureLoadTargets.ContainsKey(s)) return;
            ReplaceTexture((Texture2D)mat.mainTexture, path, s);
        }

        private static string GetAtlasTextureName(Image i)
        {
            var rect = i.sprite.textureRect;
            return $"[{rect.width},{rect.height},{rect.x},{rect.y}]{i.mainTexture.name}";
        }

        internal static void ReplaceTexture(Image img, string path, string s)
        {
            ReplaceTexture(img.material, path, s);
            if (img.sprite != null)
            {
                if (!textureLoadTargets.ContainsKey(s)) return;

                var tex = img.mainTexture;
                if (tex == null) return;
                var rect = img.sprite.textureRect;
                if (rect == null || rect == new Rect(0, 0, tex.width, tex.height))
                {
                    if (IsSwappedTexture(tex)) return;
                    if (string.IsNullOrEmpty(tex.name)) return;
                    if (!textureLoadTargets[s].ContainsKey(img.mainTexture.name)) return;
                    var t2d = new Texture2D(2, 2);
                    t2d.LoadImage(textureLoadTargets[s][img.mainTexture.name]);
                    img.sprite = Sprite.Create(t2d, img.sprite.rect, img.sprite.pivot);
                    tex.name = "*" + img.mainTexture.name;
                }
                else
                {
                    //Atlas
                    if (IsSwappedTexture(img.sprite.texture)) return;
                    var name = GetAtlasTextureName(img);
                    byte[] newTex = null;
                    if (textureLoadTargets[s].TryGetValue(img.mainTexture.name, out newTex))
                    {
                        img.sprite.texture.LoadImage(newTex);
                        img.sprite.texture.name = "*" + img.mainTexture.name;
                    } else if (textureLoadTargets[s].TryGetValue(name, out newTex))
                    {
                        var t2d = new Texture2D(2, 2);
                        t2d.LoadImage(newTex);
                        img.sprite = Sprite.Create(t2d, new Rect(0, 0, t2d.width, t2d.height), Vector2.zero);
                        img.sprite.texture.name = "*" + name;
                    }
                }
            }
        }

        internal static void ReplaceTexture(RawImage img, string path, string s)
        {
            ReplaceTexture(img.material, path, s);
        }

        internal static void RegisterTexture(Texture tex, string path, string s)
        {
            if (isDumpingEnabled)
            {
                if (tex == null) return;
                if (IsSwappedTexture(tex)) return;
                if (string.IsNullOrEmpty(tex.name)) return;
                PrepDumper(s);
                var tm = new TextureMetadata(tex, path, s);
                if (textureDumpTargets[s].Contains(tm)) return;
                textureDumpTargets[s].Add(tm);
                DumpTexture(tm);
            }
        }

        private static Dictionary<string, Texture2D> readableTextures = new Dictionary<string, Texture2D>();

        internal static void RegisterTexture(Image i, string path, string s)
        {
            if (isDumpingEnabled)
            {
                var tex = i.mainTexture;
                if (tex == null) return;
                if (IsSwappedTexture(tex)) return;
                if (string.IsNullOrEmpty(tex.name)) return;
                PrepDumper(s);
                RegisterTexture(i.mainTexture, path, s);
                if (i.sprite == null) return;

                var rect = i.sprite.textureRect;
                if (rect == null || rect == new Rect(0, 0, tex.width, tex.height))
                {
                    RegisterTexture(i.mainTexture, path, s);
                    return;
                }
                Texture2D readable = null;
                if (!readableTextures.TryGetValue(tex.name, out readable))
                {
                    readableTextures[tex.name] = TextureUtils.MakeReadable(tex);
                    readable = readableTextures[tex.name];
                }
                var cropped = readable.GetPixels((int)rect.x, (int)rect.y, (int)rect.width, (int)rect.height);
                var nt2d = new Texture2D((int)rect.width, (int)rect.height);
                nt2d.SetPixels(cropped);
                nt2d.Apply();

                nt2d.name = GetAtlasTextureName(i);
                var tm = new TextureMetadata(nt2d, path, s);

                if (textureDumpTargets[s].Contains(tm)) return;
                textureDumpTargets[s].Add(tm);
                DumpTexture(tm);
            }
        }

        internal static void ReplaceTexture(ref Sprite spr, string path, string s)
        {
            if (spr == null || spr.texture == null) return;
            if (!textureLoadTargets.ContainsKey(s)) return;
            if (!textureLoadTargets[s].ContainsKey(spr.texture.name)) return;    //TODO: Hash?
            if (IsSwappedTexture(spr.texture)) return;
            var tex = textureLoadTargets[s][spr.texture.name];

            if (spr.texture.name.ToLower().Contains("atlas"))
            {
                spr.texture.LoadImage(tex);
            }
            else
            {
                var t2d = new Texture2D(2, 2);
                t2d.LoadImage(tex);
                spr = Sprite.Create(t2d, spr.rect, spr.pivot);
            }

            spr.texture.name = "*" + spr.texture.name;
        }

        internal static void RegisterSpriteState(ref SpriteState sprState, string path, string s)
        {
            if (sprState.disabledSprite != null)
            {
                RegisterTexture(sprState.disabledSprite?.texture, path, s);
                var spr = sprState.disabledSprite;
                ReplaceTexture(ref spr, path, s);
                sprState.disabledSprite = spr;
            }
            if (sprState.highlightedSprite != null)
            {
                RegisterTexture(sprState.highlightedSprite?.texture, path, s);
                var spr = sprState.highlightedSprite;
                ReplaceTexture(ref spr, path, s);
                sprState.highlightedSprite = spr;
            }
            if (sprState.pressedSprite != null)
            {
                RegisterTexture(sprState.pressedSprite?.texture, path, s);
                var spr = sprState.pressedSprite;
                ReplaceTexture(ref spr, path, s);
                sprState.pressedSprite = spr;
            }
        }

        internal static void RegisterSprites(ref Sprite[] sprites, string path, string s)
        {
            for (var i = 0; i < sprites.Length; i++)
            {
                if (sprites[i] != null)
                {
                    RegisterTexture(sprites[i]?.texture, path, s);
                    var spr = sprites[i];
                    ReplaceTexture(ref spr, path, s);
                    sprites[i] = spr;
                }
            }
        }

        private static void TranslateButton(Button b, string path, string scene)
        {
            var ss = b.spriteState;
            RegisterSpriteState(ref ss, path, scene);
            b.spriteState = ss;
        }

        private static void TranslateRawImage(RawImage ri, string path, string scene)
        {
            RegisterTexture(ri.mainTexture, path, scene);
            ReplaceTexture(ri, path, scene);
        }

        private static void TranslateImage(Image i, string path, string scene)
        {
            RegisterTexture(i, path, scene);
            ReplaceTexture(i, path, scene);
        }

        private static void TranslateHSpriteChangeCtrl(H.SpriteChangeCtrl hscc, string path, string scene)
        {
            var sprs = hscc.sprites;
            RegisterSprites(ref sprs, path, scene);
            hscc.sprites = sprs;
        }

        public static void TranslateComponents(GameObject go)
        {
            var zettai = GameObjectUtils.AbsoluteTransform(go);
            var scene = go.scene.name;
            if (scene == "DontDestroyOnLoad")
                scene = SceneManager.GetActiveScene().name;
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp is Image) TranslateImage((Image)comp, zettai, scene);
                else if (comp is RawImage) TranslateRawImage((RawImage)comp, zettai, scene);
                else if (comp is Button) TranslateButton((Button)comp, zettai, scene);
                else if (comp is H.SpriteChangeCtrl) TranslateHSpriteChangeCtrl((H.SpriteChangeCtrl)comp, zettai, scene);
            }
        }

        internal static void DumpTexture(TextureMetadata tm)
        {
            var dir = $"{TL_DIR_SCENE}/{tm.scene}";
            var di = new DirectoryInfo(dir);
            if (!di.Exists) di.Create();
            var path = $"{dir}/{tm.SafeID}.png";
            if (!File.Exists(path)) TextureUtils.SaveTex(tm.texture, path);
            var sw = sw_textureNameDump[tm.scene];
            if (sw == null) return;
            //if (sw.BaseStream == null) return;
            sw.WriteLine(string.Format("{0}={1}", tm.SafeID, path.Replace(TL_DIR_SCENE + "/", "")));
            sw.Flush();
        }

		#endregion


		#region MonoBehaviour
		void OnEnable()
		{
			SceneManager.sceneLoaded += LevelFinishedLoading;
		}

		void OnDisable()
		{
			SceneManager.sceneLoaded -= LevelFinishedLoading;
		}
		#endregion

		#region Scenario & Communication Translation

		private static FieldInfo f_commandPacks =
            typeof(TextScenario).GetField("commandPacks", BindingFlags.NonPublic | BindingFlags.Instance);
        
        private static readonly string scenarioDir = Path.Combine(Utility.PluginsDirectory, "translation\\scenario");
        private static readonly string communicationDir = Path.Combine(Utility.PluginsDirectory, "translation\\communication");


        public static T ManualLoadAsset<T>(string bundle, string asset, string manifest) where T : UnityEngine.Object
        {
            string path = $@"{Application.dataPath}\..\{(string.IsNullOrEmpty(manifest) ? "abdata" : manifest)}\{bundle}";

            AssetBundleManager.LoadAssetBundleInternal(bundle, false, manifest);
            var assetBundle = AssetBundleManager.GetLoadedAssetBundle(bundle, out string error, manifest);

            T output = assetBundle.m_AssetBundle.LoadAsset<T>(asset);

            return output;
        }

        protected IEnumerable<IEnumerable<string>> SplitAndEscape(string source)
        {
            StringBuilder bodyBuilder = new StringBuilder();

            // here we build rows, one by one
            int i = 0;
            var row = new List<string>();
            var limit = source.Length;
            bool inQuote = false;

            while (i < limit)
            {
                if (source[i] == '\r')
                {
                    //( ͠° ͜ʖ °)
                }
                else if (source[i] == ',' && !inQuote)
                {
                    row.Add(bodyBuilder.ToString());
                    bodyBuilder.Length = 0; //.NET 2.0 ghetto clear
                }
                else if (source[i] == '\n' && !inQuote)
                {
                    if (bodyBuilder.Length != 0 || row.Count != 0)
                    {
                        row.Add(bodyBuilder.ToString());
                        bodyBuilder.Length = 0; //.NET 2.0 ghetto clear
                    }

                    yield return row;
                    row.Clear();
                }
                else if (source[i] == '"')
                {
                    if (!inQuote)
                        inQuote = true;
                    else
                    {
                        if (i + 1 < limit
                            && source[i + 1] == '"')
                        {
                            bodyBuilder.Append('"');
                            i++;
                        }
                        else
                            inQuote = false;
                    }
                }
                else
                {
                    bodyBuilder.Append(source[i]);
                }

                i++;
            }

            if (bodyBuilder.Length > 0)
                row.Add(bodyBuilder.ToString());

            if (row.Count > 0)
                yield return row;
        }


        protected bool RedirectHook(string assetBundleName, string assetName, Type type, string manifestAssetBundleName, out AssetBundleLoadAssetOperation result)
        {
            if (type == typeof(ScenarioData))
            {
                string scenarioPath = Path.Combine(scenarioDir, Path.Combine(assetBundleName, $"{assetName}.csv")).Replace('/', '\\').Replace(".unity3d", "").Replace(@"adv\scenario\", "");
                
                if (File.Exists(scenarioPath))
                {
                    var rawData = ManualLoadAsset<ScenarioData>(assetBundleName, assetName, manifestAssetBundleName);

                    rawData.list.Clear();

                    foreach (IEnumerable<string> line in SplitAndEscape(File.ReadAllText(scenarioPath, Encoding.UTF8)))
                    {
                        string[] data = line.ToArray();

                        string[] args = new string[data.Length - 4];

                        Array.Copy(data, 4, args, 0, args.Length);

                        ScenarioData.Param param = new ScenarioData.Param(bool.Parse(data[3]), (Command)int.Parse(data[2]), args);

                        param.SetHash(int.Parse(data[0]));

                        rawData.list.Add(param);
                    }

                    result = new AssetBundleLoadAssetOperationSimulation(rawData);
                    return true;
                }
            }
            else if (type == typeof(ExcelData))
            {
                string communicationPath = Path.Combine(communicationDir, Path.Combine(assetBundleName.Replace("communication/", ""), $"{assetName}.csv")).Replace('/', '\\').Replace(".unity3d", "");
                

                Logger.Log(LogLevel.Debug, communicationPath);
                if (File.Exists(communicationPath))
                {
                    Logger.Log(LogLevel.Debug, "Loaded!");
                    var rawData = ManualLoadAsset<ExcelData>(assetBundleName, assetName, manifestAssetBundleName);

                    //int i = 0;
                    //foreach (IEnumerable<string> line in SplitAndEscape(File.ReadAllText(communicationPath, Encoding.UTF8)))
                    //{
                    //    var list = line.ToList();
                        
                    //    Logger.Log(LogLevel.Debug, $"orig ({i}): {string.Join(",", rawData.list[i].list.ToArray())}");
                    //    Logger.Log(LogLevel.Debug, $"modf ({i}): {string.Join(",", list.ToArray())}");
                    //    i++;
                    //}

                    rawData.list.Clear();
                    
                    foreach (IEnumerable<string> line in SplitAndEscape(File.ReadAllText(communicationPath, Encoding.UTF8)))
                    {
                        var list = line.ToList();

                        ExcelData.Param param = new ExcelData.Param
                        {
                            list = list
                        };

                        //for (int i = 0; i < param.list.Count; i++)
                        //{
                        //    if (param.list[i].Trim().StartsWith("「"))
                        //        //&& param.list[15].EndsWith("」"))
                        //        param.list[i] = "tupac still alive in serbia";
                        //}

                        
                        //if (param.list.Count > 16 && !string.IsNullOrEmpty(param.list[16])
                        //                          && param.list[15].StartsWith("「"))
                        //    //&& param.list[15].EndsWith("」"))
                        //    param.list[16] = "tupac still alive in serbia";
                        

                        rawData.list.Add(param);
                    }

                    result = new AssetBundleLoadAssetOperationSimulation(rawData);
                    return true;
                }
            }

            result = null;
            return false;
        }

        #endregion
    }
}