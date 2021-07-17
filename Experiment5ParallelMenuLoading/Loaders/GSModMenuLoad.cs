﻿using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ShortMenuLoader
{
	internal class GSModMenuLoad
	{
		public static readonly Dictionary<string, string> FilesDictionary = new Dictionary<string, string>();
		public static bool DictionaryBuilt = false;
		private static readonly Dictionary<string, MemoryStream> FilesToRead = new Dictionary<string, MemoryStream>();

		private const string CacheFileName = "ShortMenuLoaderCache.json";
		private static bool CacheLoadDone = false;
		private static Dictionary<string, MenuStub> MenuCache = new Dictionary<string, MenuStub>();
		public static IEnumerator LoadCache(int Retry = 0)
		{

			CacheLoadDone = false;

			Task cacheLoader = Task.Factory.StartNew(new Action(() =>
			{
				if (File.Exists(BepInEx.Paths.ConfigPath + "\\" + CacheFileName))
				{
					string jsonString = File.ReadAllText(BepInEx.Paths.ConfigPath + "\\" + CacheFileName);

					var tempDic = JsonConvert.DeserializeObject<Dictionary<string, MenuStub>>(jsonString);

					MenuCache = tempDic;
				}

				CacheLoadDone = true;
			}));

			while (cacheLoader.IsCompleted == false)
			{
				yield return null;
			}

			if (cacheLoader.IsFaulted)
			{
				if (Retry < 3)
				{
					Main.logger.LogError($"There was an error while attempting to load the mod cache: \n{cacheLoader.Exception.InnerException.Message}\n{cacheLoader.Exception.InnerException.StackTrace}\n\nAn attempt will be made to restart the load task...");

					yield return new WaitForSecondsRealtime(5);

					Main.@this2.StartCoroutine(LoadCache(++Retry));
				}
				else
				{
					Main.logger.LogError($"There was an error while attempting to load the mod cache: \n{cacheLoader.Exception.InnerException.Message}\n{cacheLoader.Exception.InnerException.StackTrace}\n\nThis is the 4th attempt to kickstart the task. Cache will be deleted and rebuilt next time.");

					MenuCache = new Dictionary<string, MenuStub>();

					File.Delete(BepInEx.Paths.ConfigPath + "\\" + CacheFileName);

					CacheLoadDone = true;
				}
			}
		}
		public static IEnumerator SaveCache(int Retry = 0)
		{
			Task cacheSaver = Task.Factory.StartNew(new Action(() =>
			{
				MenuCache = MenuCache
				.Where(k => FilesDictionary.Keys.Contains(k.Key))
				.ToDictionary(t => t.Key, l => l.Value);

				File.WriteAllText(BepInEx.Paths.ConfigPath + "\\" + CacheFileName, JsonConvert.SerializeObject(MenuCache));

				Main.logger.LogDebug("Finished cleaning and saving the mod cache...");
			}));

			while (cacheSaver.IsCompleted == false)
			{
				yield return null;
			}

			if (cacheSaver.IsFaulted)
			{
				if (Retry < 3)
				{
					Main.logger.LogError($"Cache saver task failed due to an unexpected error! This is considered a minor failure: {cacheSaver.Exception.InnerException.Message}\n{cacheSaver.Exception.InnerException.StackTrace}\n\nAn attempt will be made to restart the task again...");

					yield return new WaitForSecondsRealtime(5);

					Main.@this2.StartCoroutine(SaveCache(++Retry));

					yield break;
				}
				else
				{
					Main.logger.LogFatal($"Cache saver task failed due to an unexpected error! This is considered a minor failure: {cacheSaver.Exception.InnerException.Message}\n{cacheSaver.Exception.InnerException.StackTrace}\n\nNo further attempts will be made to start the task again...");

					throw cacheSaver.Exception.InnerException;
				}
			}
		}
		public static IEnumerator GSMenuLoadStart(List<SceneEdit.SMenuItem> menuList, Dictionary<int, List<int>> menuGroupMemberDic)
		{
			HashSet<SceneEdit.SMenuItem> listOfLoads = new HashSet<SceneEdit.SMenuItem>();
			List<string> listOfDuplicates = new List<string>();

			string path = BepInEx.Paths.GameRootPath;

			FilesToRead.Clear();
			FilesDictionary.Clear();

			DictionaryBuilt = false;

			Main.logger.LogDebug("Started worker...");

			Task loaderWorker = Task.Factory.StartNew(new Action(() =>
			{

				foreach (string s in Directory.GetFiles(path + "\\Mod", "*.menu", SearchOption.AllDirectories))
				{
					if (!FilesDictionary.ContainsKey(Path.GetFileName(s).ToLower()))
					{
						FilesDictionary[Path.GetFileName(s).ToLower()] = s;
					}
					else
					{
						listOfDuplicates.Add(s);
					}
				}

				Main.logger.LogDebug("Worker done fetching files...");

				DictionaryBuilt = true;

				while (CacheLoadDone != true)
				{
					Thread.Sleep(1);
				}

				Mutex dicLock = new Mutex();

				Main.logger.LogDebug("Worker started loading menus into memory...");

				Task servant = Task.Factory.StartNew(new Action(() =>
				{
					foreach (string s in FilesDictionary.Keys)
					{
						try
						{
							if (MenuCache.ContainsKey(s.ToLower()))
							{
								dicLock.WaitOne();
								FilesToRead[s.ToLower()] = null;
								dicLock.ReleaseMutex();
							}
							else
							{
								dicLock.WaitOne();
								FilesToRead[s.ToLower()] = new MemoryStream(File.ReadAllBytes(FilesDictionary[s]));
								dicLock.ReleaseMutex();
							}

						}
						catch
						{

							dicLock.WaitOne();
							FilesToRead[s.ToLower()] = null;
							dicLock.ReleaseMutex();

						}
					}
				}));

				Main.logger.LogDebug("Menu load worker started operating...");

				while (servant.IsCompleted == false || FilesToRead.Count > 0)
				{
					if (FilesToRead.Count == 0)
					{
						Thread.Sleep(1);
						continue;
					}

					dicLock.WaitOne();
					string strFileName = FilesToRead.FirstOrDefault().Key;
					dicLock.ReleaseMutex();

					SceneEdit.SMenuItem mi2 = new SceneEdit.SMenuItem();
					//SceneEdit.GetMenuItemSetUP causes crash if parallel threaded. Our implementation is thread safe-ish.
					if (GSModMenuLoad.GetMenuItemSetUP(mi2, strFileName, out string iconLoad))
					{
						if (iconLoad != null)
						{
							listOfLoads.Add(mi2);
							QuickEdit.idItemDic[mi2.m_nMenuFileRID] = mi2;
							QuickEdit.texFileIDDic[mi2.m_nMenuFileRID] = iconLoad;
							mi2.m_texIcon = Texture2D.whiteTexture;
						}
					}
					dicLock.WaitOne();
					FilesToRead.Remove(strFileName);
					dicLock.ReleaseMutex();
				}

				if (servant.IsFaulted)
				{
					Main.logger.LogError($"Servant task failed due to an unexpected error!");

					throw servant.Exception;
				}
			}));

			//We wait until the manager is not busy because starting work while the manager is busy causes egregious bugs.
			while (!loaderWorker.IsCompleted || GameMain.Instance.CharacterMgr.IsBusy())
			{
				yield return null;
			}

			if (loaderWorker.IsFaulted)
			{
				Main.logger.LogError($"Worker task failed due to an unexpected error! This is considered a full failure: {loaderWorker.Exception.InnerException.Message}\n{loaderWorker.Exception.InnerException.StackTrace}\n\nwe will try restarting the load task...");

				yield return new WaitForSecondsRealtime(2);

				Main.@this.StartCoroutine(GSModMenuLoad.GSMenuLoadStart(menuList, menuGroupMemberDic));

				yield break;
			}

			Main.logger.LogDebug("Worker finished! Now continuing foreach...");

			Main.logger.LogDebug("lilly:" + listOfLoads.Count);
			foreach (SceneEdit.SMenuItem mi2 in listOfLoads)
			{
				try
				{

					// 우선 순위 변경
					if (Main.ChangeModPriority.Value)
					{
						if (mi2.m_fPriority <= 0)
						{
							mi2.m_fPriority = 1f;
						}

						mi2.m_fPriority += 10000;
					}

					if (!mi2.m_bMan)
					{
						AccessTools.Method(typeof(SceneEdit), "AddMenuItemToList").Invoke(Main.@this, new object[] { mi2 });
						menuList.Add(mi2);
						Main.@this.m_menuRidDic[mi2.m_nMenuFileRID] = mi2;
						string parentMenuName2 = AccessTools.Method(typeof(SceneEdit), "GetParentMenuFileName").Invoke(Main.@this, new object[] { mi2 }) as string;
						if (!string.IsNullOrEmpty(parentMenuName2))
						{
							int hashCode2 = parentMenuName2.GetHashCode();
							if (!menuGroupMemberDic.ContainsKey(hashCode2))
							{
								menuGroupMemberDic.Add(hashCode2, new List<int>());
							}
							menuGroupMemberDic[hashCode2].Add(mi2.m_strMenuFileName.ToLower().GetHashCode());
						}
						else if (mi2.m_strCateName.IndexOf("set_") != -1 && mi2.m_strMenuFileName.IndexOf("_del") == -1)
						{
							mi2.m_bGroupLeader = true;
							mi2.m_listMember = new List<SceneEdit.SMenuItem>
						{
							mi2
						};
						}
					}
				}
				catch (Exception e)
				{
					Main.logger.LogError($"We caught the following exception while processing {mi2.m_strMenuFileName}:\n {e.StackTrace}");
				}
				if (Main.BreakInterval.Value < Time.realtimeSinceStartup - Main.time)
				{
					yield return null;
					Main.time = Time.realtimeSinceStartup;
				}
			}

			Main.ThreadsDone++;
			Main.logger.LogInfo($"Standard mods finished loading at: {Main.WatchOverall.Elapsed}");

			Main.@this.StartCoroutine(SaveCache());




			if (listOfDuplicates.Count > 0)
			{
				Main.logger.LogWarning($"There are {listOfDuplicates.Count} duplicate menus in your mod folder!");

				foreach (string s in listOfDuplicates)
				{
					Main.logger.LogWarning("We found a duplicate that should be corrected immediately in your mod folder at: " + s);
				}
			}
		}

		public static bool GetMenuItemSetUP(SceneEdit.SMenuItem mi, string f_strMenuFileName, out string IconTex)
		{
			IconTex = null;

			if (f_strMenuFileName.Contains("_zurashi"))
			{
				return false;
			}
			if (f_strMenuFileName.Contains("_mekure"))
			{
				return false;
			}
			f_strMenuFileName = Path.GetFileName(f_strMenuFileName);
			mi.m_strMenuFileName = f_strMenuFileName;
			mi.m_nMenuFileRID = f_strMenuFileName.ToLower().GetHashCode();
			try
			{
				if (!GSModMenuLoad.InitMenuItemScript(mi, f_strMenuFileName, out IconTex))
				{
					Main.logger.LogError("(メニュースクリプトが読めませんでした。) The following menu file could not be read and will be skipped: " + f_strMenuFileName);
				}
			}
			catch (Exception ex)
			{
				Main.logger.LogError(string.Concat(new string[]
				{
				"GetMenuItemSetUP tossed an exception while reading: ",
				f_strMenuFileName,
				"\n\n",
				ex.Message,
				"\n",
				ex.StackTrace
				}));
				return false;
			}

			return true;
		}
		public static bool InitMenuItemScript(SceneEdit.SMenuItem mi, string f_strMenuFileName, out string IconTex)
		{
			IconTex = null;

			if (f_strMenuFileName.IndexOf("mod_") == 0)
			{
				string modPathFileName = Menu.GetModPathFileName(f_strMenuFileName);
				return !string.IsNullOrEmpty(modPathFileName) && SceneEdit.InitModMenuItemScript(mi, modPathFileName);
			}

			if (MenuCache.ContainsKey(f_strMenuFileName))
			{
				try
				{
					MenuStub tempStub = MenuCache[f_strMenuFileName];
					if (tempStub.DateModified == File.GetLastWriteTimeUtc(FilesDictionary[f_strMenuFileName]))
					{
						if (tempStub.Name != null)
						{
							mi.m_strMenuName = tempStub.Name;
						}

						if (tempStub.Description != null)
						{
							mi.m_strInfo = tempStub.Description;
						}

						if (tempStub.Category != null)
						{
							mi.m_strCateName = tempStub.Category;
							mi.m_mpn = (MPN)Enum.Parse(typeof(MPN), tempStub.Category);
						}
						else
						{
							mi.m_mpn = MPN.null_mpn;
						}

						if (tempStub.ColorSetMPN != null)
						{
							mi.m_eColorSetMPN = (MPN)Enum.Parse(typeof(MPN), tempStub.ColorSetMPN);
						}

						if (tempStub.ColorSetMenu != null)
						{
							mi.m_strMenuNameInColorSet = tempStub.ColorSetMenu;
						}

						if (tempStub.MultiColorID == null)
						{
							mi.m_pcMultiColorID = MaidParts.PARTS_COLOR.NONE;
						}
						else if (tempStub.MultiColorID != null)
						{
							mi.m_pcMultiColorID = (MaidParts.PARTS_COLOR)Enum.Parse(typeof(MaidParts.PARTS_COLOR), tempStub.MultiColorID);
						}

						mi.m_boDelOnly = tempStub.DelMenu;

						mi.m_fPriority = tempStub.Priority;

						mi.m_bMan = tempStub.ManMenu;

						IconTex = tempStub.Icon;

						return true;
					}
					else
					{
						Main.logger.LogWarning($"A cache entry was found outdated. This should be automatically fixed and the cache reloaded.");
					}
				}
				catch (Exception ex)
				{
					Main.logger.LogError(string.Concat(new string[]
					{
						$"Encountered an issue while trying to load menu {f_strMenuFileName} from cache. This should be automatically fixed and the cache reloaded.",
						"\n\n",
						ex.Message,
						"\n",
						ex.StackTrace
					}));
				}
			}

			try
			{
				if (FilesToRead[f_strMenuFileName] == null)
				{
					FilesToRead[f_strMenuFileName] = new MemoryStream(File.ReadAllBytes(FilesDictionary[f_strMenuFileName]));
				}
			}
			catch (Exception ex)
			{
				Main.logger.LogError(string.Concat(new string[]
				{
						"The following menu file could not be read! (メニューファイルがが読み込めませんでした。): ",
						f_strMenuFileName,
						"\n\n",
						ex.Message,
						"\n",
						ex.StackTrace
				}));

				return false;
			}

			string text6 = string.Empty;
			string text7 = string.Empty;
			string path = "";

			MenuStub cacheEntry = new MenuStub();

			try
			{
				cacheEntry.DateModified = File.GetLastWriteTimeUtc(FilesDictionary[f_strMenuFileName]);

				BinaryReader binaryReader = new BinaryReader(FilesToRead[f_strMenuFileName], Encoding.UTF8);
				string text = binaryReader.ReadString();

				if (text != "CM3D2_MENU")
				{
					Main.logger.LogError("ProcScriptBin (例外 : ヘッダーファイルが不正です。) The header indicates a file type that is not a menu file!" + text + " @ " + f_strMenuFileName);

					return false;
				}

				binaryReader.ReadInt32();
				path = binaryReader.ReadString();
				binaryReader.ReadString();
				binaryReader.ReadString();
				binaryReader.ReadString();
				binaryReader.ReadInt32();
				string text5 = null;

				while (true)
				{
					int num4 = binaryReader.ReadByte();
					text7 = text6;
					text6 = string.Empty;
					if (num4 == 0)
					{
						break;
					}
					for (int i = 0; i < num4; i++)
					{
						text6 = text6 + "\"" + binaryReader.ReadString() + "\" ";
					}
					if (!(text6 == string.Empty))
					{
						string stringCom = UTY.GetStringCom(text6);
						string[] stringList = UTY.GetStringList(text6);
						if (stringCom == "name")
						{
							if (stringList.Length > 1)
							{
								string text8 = stringList[1];
								string text9 = string.Empty;
								string arg = string.Empty;
								int j = 0;
								while (j < text8.Length && text8[j] != '\u3000' && text8[j] != ' ')
								{
									text9 += text8[j];
									j++;
								}
								while (j < text8.Length)
								{
									arg += text8[j];
									j++;
								}
								mi.m_strMenuName = text9;
								cacheEntry.Name = mi.m_strMenuName;
							}
							else
							{
								Main.logger.LogWarning("Menu file has no name and an empty description will be used instead." + " @ " + f_strMenuFileName);

								mi.m_strMenuName = "";
								cacheEntry.Name = mi.m_strMenuName;
							}
						}
						else if (stringCom == "setumei")
						{
							if (stringList.Length > 1)
							{
								mi.m_strInfo = stringList[1];
								mi.m_strInfo = mi.m_strInfo.Replace("《改行》", "\n");
								cacheEntry.Description = mi.m_strInfo;
							}
							else
							{
								Main.logger.LogWarning("Menu file has no description (setumei) and an empty description will be used instead." + " @ " + f_strMenuFileName);

								mi.m_strInfo = "";
								cacheEntry.Description = mi.m_strInfo;
							}
						}
						else if (stringCom == "category")
						{
							if (stringList.Length > 1)
							{
								string strCateName = stringList[1].ToLower();
								mi.m_strCateName = strCateName;
								cacheEntry.Category = mi.m_strCateName;
								try
								{
									mi.m_mpn = (MPN)Enum.Parse(typeof(MPN), mi.m_strCateName);
									cacheEntry.Category = mi.m_mpn.ToString();
								}
								catch
								{
									Main.logger.LogWarning("There is no category called (カテゴリがありません。): " + mi.m_strCateName + " @ " + f_strMenuFileName);
									return false;
								}
							}
							else
							{
								Main.logger.LogWarning("The following menu file has a category parent with no category: " + f_strMenuFileName);
								return false;
							}
						}
						else if (stringCom == "color_set")
						{
							if (stringList.Length > 1)
							{
								try
								{
									mi.m_eColorSetMPN = (MPN)Enum.Parse(typeof(MPN), stringList[1].ToLower());
									cacheEntry.ColorSetMPN = mi.m_eColorSetMPN.ToString();
								}
								catch
								{
									Main.logger.LogWarning("There is no category called(カテゴリがありません。): " + mi.m_strCateName + " @ " + f_strMenuFileName);

									return false;
								}
								if (stringList.Length >= 3)
								{
									mi.m_strMenuNameInColorSet = stringList[2].ToLower();
									cacheEntry.ColorSetMenu = mi.m_strMenuNameInColorSet;
								}
							}
							else
							{
								Main.logger.LogWarning("A color_set entry exists but is otherwise empty" + " @ " + f_strMenuFileName);
							}
						}
						else if (stringCom == "tex" || stringCom == "テクスチャ変更")
						{
							MaidParts.PARTS_COLOR pcMultiColorID = MaidParts.PARTS_COLOR.NONE;
							if (stringList.Length == 6)
							{
								string text10 = stringList[5];
								try
								{
									pcMultiColorID = (MaidParts.PARTS_COLOR)Enum.Parse(typeof(MaidParts.PARTS_COLOR), text10.ToUpper());
								}
								catch
								{
									Main.logger.LogError("無限色IDがありません。(The following free color ID does not exist: )" + text10 + " @ " + f_strMenuFileName);

									return false;
								}
								mi.m_pcMultiColorID = pcMultiColorID;
								cacheEntry.MultiColorID = mi.m_pcMultiColorID.ToString();
							}
						}
						else if (stringCom == "icon" || stringCom == "icons")
						{
							if (stringList.Length > 1)
							{
								text5 = stringList[1];
							}
							else
							{
								Main.logger.LogError("The following menu file has an icon entry but no field set: " + f_strMenuFileName);

								return false;
							}
						}
						else if (stringCom == "saveitem")
						{
							if (stringList.Length > 1)
							{
								string text11 = stringList[1];
								if (String.IsNullOrEmpty(text11))
								{
									Main.logger.LogWarning("SaveItem is either null or empty." + " @ " + f_strMenuFileName);
								}
							}
							else
							{
								Main.logger.LogWarning("A saveitem entry exists with nothing set in the field @ " + f_strMenuFileName);
							}
						}
						else if (stringCom == "unsetitem")
						{
							mi.m_boDelOnly = true;
							cacheEntry.DelMenu = mi.m_boDelOnly;
						}
						else if (stringCom == "priority")
						{
							if (stringList.Length > 1)
							{
								mi.m_fPriority = float.Parse(stringList[1]);
								cacheEntry.Priority = mi.m_fPriority;
							}
							else
							{
								Main.logger.LogError("The following menu file has a priority entry but no field set. A default value of 10000 will be used: " + f_strMenuFileName);

								mi.m_fPriority = 10000f;
								cacheEntry.Priority = mi.m_fPriority;
							}
						}
						else if (stringCom == "メニューフォルダ")
						{
							if (stringList.Length > 1)
							{
								if (stringList[1].ToLower() == "man")
								{
									mi.m_bMan = true;
									cacheEntry.ManMenu = mi.m_bMan;
								}
							}
							else
							{
								Main.logger.LogError("A a menu with a menu folder setting (メニューフォルダ) has an entry but no field set: " + f_strMenuFileName);

								return false;
							}
						}
					}
				}

				if (!String.IsNullOrEmpty(text5))
				{
					try
					{
						IconTex = text5;
						cacheEntry.Icon = text5;
						//mi.m_texIcon = ImportCM.CreateTexture(text5);
					}
					catch (Exception)
					{
						Main.logger.LogError("Error setting some icon tex from a normal mod." + " @ " + f_strMenuFileName);

						return false;
					}
				}
				binaryReader.Close();
			}
			catch (Exception ex2)
			{
				Main.logger.LogError(string.Concat(new string[]
				{
				"Exception when reading: ",
				f_strMenuFileName,
				"\nThe line currently being processed, likely the issue (現在処理中だった行): ",
				text6,
				"\nPrevious line (以前の行): ",
				text7,
				"\n\n",
				ex2.Message,
				"\n",
				ex2.StackTrace
				}));

				return false;
			}
			MenuCache[f_strMenuFileName] = cacheEntry;
			return true;
		}
	}
}
