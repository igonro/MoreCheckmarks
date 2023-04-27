﻿using UnityEngine;
using UnityEngine.UI;
using HarmonyLib;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using System;
using EFT;
using EFT.UI.DragAndDrop;
using System.Reflection;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.Interactive;
using EFT.Quests;
using System.Linq;
using TMPro;
using BepInEx;
using Aki.Common.Http;


// We want to get access to the list of availabe loot item actions when we look at loose loot sowe can change color of "Take" action
// GClass1767 has static method GetAvailableActions(GamePlayerOwner owner, [CanBeNull] GInterface85 interactive) to get list of actions available for the interactive
// This calls GClass1767.smethod_3 if the interactive is a LootItem
// This returns an instance of GClass2644 which has a list field "Actions" containing all available actions of type GClass2643
// GClass2643.Name will be directly used as the string that will be displayed in the list, so we set it to a TMPro string with correct color and bold
using InteractionController = GClass1767;
using InteractionInstance = GClass2644;
using Action = GClass2643;

namespace MoreCheckmarks
{
    public struct NeededStruct
    {
        public bool foundNeeded;
        public bool foundFulfilled;
        public int possessedCount;
        public int requiredCount;
    }

    [BepInPlugin(pluginGuid, pluginName, pluginVersion)]
    public class MoreCheckmarksMod : BaseUnityPlugin
    {
        // BepinEx
        public const string pluginGuid = "VIP.TommySoucy.MoreCheckmarks";
        public const string pluginName = "MoreCheckmarks";
        public const string pluginVersion = "1.4.3";

        // Config settings
        public static bool fulfilledAnyCanBeUpgraded = false;
        public static int questPriority = 0;
        public static int hideoutPriority = 1;
        public static int wishlistPriority = 2;
        public static bool showLockedModules = true;
        public static Color needMoreColor = new Color(1, 0.37255f, 0.37255f);
        public static Color fulfilledColor = new Color(0.30588f, 1, 0.27843f);
        public static Color wishlistColor = new Color(0.23137f, 0.93725f, 1);
        public static bool includeFutureQuests = true;

        // Assets
        public static JObject config;
        public static Sprite whiteCheckmark;
        private static TMP_FontAsset benderBold;
        public static string modPath;

        // Live
        public static MoreCheckmarksMod modInstance;
        // Quest IDs and Names by items in their requirements
        public static Dictionary<string, QuestPair> questDataStartByItemTemplateID = new Dictionary<string, QuestPair>();
        public static Dictionary<string, Dictionary<string, int>> neededStartItemsByQuest = new Dictionary<string, Dictionary<string, int>>();
        public static Dictionary<string, QuestPair> questDataCompleteByItemTemplateID = new Dictionary<string, QuestPair>();
        public static Dictionary<string, Dictionary<string, int>> neededCompleteItemsByQuest = new Dictionary<string, Dictionary<string, int>>();
        public class QuestPair
        {
            public Dictionary<string, string> questData = new Dictionary<string, string>();
            public int count = 0;
        }

        // Patch
        public static bool setColor = false;

        private void Start()
        {
            Logger.LogInfo("MoreCheckmarks Started");

            modInstance = this;

            Init();
        }

        private void Init()
        {
            modPath = Path.GetDirectoryName(System.Reflection.Assembly.GetAssembly(typeof(MoreCheckmarksMod)).Location);
            modPath.Replace('\\', '/');

            LoadConfig();

            LoadAssets();

            LoadData();

            DoPatching();
        }

        public void LoadData()
        {
            LogInfo("Loading data");
            JArray questData = JArray.Parse(RequestHandler.GetJson("/MoreCheckmarksRoutes/quests", false));
            questDataStartByItemTemplateID.Clear();
            neededStartItemsByQuest.Clear();
            questDataCompleteByItemTemplateID.Clear();
            neededCompleteItemsByQuest.Clear();

            for (int i=0; i<questData.Count; ++i)
            {
                JArray availableForFinishConditions = questData[i]["conditions"]["AvailableForFinish"] as JArray;
                for(int j=0; j< availableForFinishConditions.Count; ++j)
                {
                    if (availableForFinishConditions[j]["_parent"].ToString().Equals("HandoverItem"))
                    {
                        JArray targets = availableForFinishConditions[j]["_props"]["target"] as JArray;
                        for (int k = 0; k< targets.Count; ++k)
                        {
                            if (questDataCompleteByItemTemplateID.TryGetValue(targets[k].ToString(), out QuestPair quests))
                            {
                                if (!quests.questData.ContainsKey(questData[i]["_id"].ToString()))
                                {
                                    quests.questData.Add(questData[i]["_id"].ToString(), questData[i]["QuestName"].ToString());
                                }
                                int.TryParse(availableForFinishConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                quests.count += parsedValue;
                            }
                            else
                            {
                                QuestPair newPair = new QuestPair();
                                newPair.questData.Add(questData[i]["_id"].ToString(), questData[i]["QuestName"].ToString());
                                int.TryParse(availableForFinishConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                newPair.count = parsedValue;
                                questDataCompleteByItemTemplateID.Add(targets[k].ToString(), newPair);
                            }

                            if(neededCompleteItemsByQuest.TryGetValue(questData[i]["_id"].ToString(), out Dictionary<string, int> items))
                            {
                                if (!items.ContainsKey(targets[k].ToString()))
                                {
                                    items.Add(targets[k].ToString(), 0);
                                }
                                int.TryParse(availableForFinishConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                items[targets[k].ToString()] += parsedValue;
                            }
                            else
                            {
                                Dictionary<string, int> newDict = new Dictionary<string, int>();
                                int.TryParse(availableForFinishConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                newDict.Add(targets[k].ToString(), parsedValue);
                                neededCompleteItemsByQuest.Add(questData[i]["_id"].ToString(), newDict);
                            }
                        }
                    }

                    if (availableForFinishConditions[j]["_parent"].ToString().Equals("FindItem"))
                    {
                        JArray targets = availableForFinishConditions[j]["_props"]["target"] as JArray;
                        for (int k = 0; k< targets.Count; ++k)
                        {
                            // Check if there is a hand in item condition for the same item and at least the same count
                            // If so skip this, we will count the hand in instead
                            bool foundInHandin = false;
                            for (int l = 0; l < availableForFinishConditions.Count; ++l)
                            {
                                if (availableForFinishConditions[l]["_parent"].ToString().Equals("HandoverItem"))
                                {
                                    JArray handInTargets = availableForFinishConditions[l]["_props"]["target"] as JArray;
                                    if (handInTargets != null && StringJArrayContainsString(handInTargets, targets[k].ToString()) && 
                                        (!int.TryParse(availableForFinishConditions[l]["_props"]["value"].ToString(), out int parsedValue) || 
                                         !int.TryParse(availableForFinishConditions[j]["_props"]["value"].ToString(), out int currentParsedValue) || 
                                         parsedValue == currentParsedValue))
                                    {
                                        foundInHandin = true;
                                        break;
                                    }
                                }
                            }
                            if (foundInHandin)
                            {
                                continue;
                            }

                            if (questDataCompleteByItemTemplateID.TryGetValue(targets[k].ToString(), out QuestPair quests))
                            {
                                if (!quests.questData.ContainsKey(questData[i]["_id"].ToString()))
                                {
                                    quests.questData.Add(questData[i]["_id"].ToString(), questData[i]["QuestName"].ToString());
                                }
                                int.TryParse(availableForFinishConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                quests.count += parsedValue;
                            }
                            else
                            {
                                QuestPair newPair = new QuestPair();
                                newPair.questData.Add(questData[i]["_id"].ToString(), questData[i]["QuestName"].ToString());
                                int.TryParse(availableForFinishConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                newPair.count = parsedValue;
                                questDataCompleteByItemTemplateID.Add(targets[k].ToString(), newPair);
                            }

                            if (neededCompleteItemsByQuest.TryGetValue(questData[i]["_id"].ToString(), out Dictionary<string, int> items))
                            {
                                if (!items.ContainsKey(targets[k].ToString()))
                                {
                                    items.Add(targets[k].ToString(), 0);
                                }
                                int.TryParse(availableForFinishConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                items[targets[k].ToString()] += parsedValue;
                            }
                            else
                            {
                                Dictionary<string, int> newDict = new Dictionary<string, int>();
                                int.TryParse(availableForFinishConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                newDict.Add(targets[k].ToString(), parsedValue);
                                neededCompleteItemsByQuest.Add(questData[i]["_id"].ToString(), newDict);
                            }
                        }
                    }

                    if (availableForFinishConditions[j]["_parent"].ToString().Equals("LeaveItemAtLocation"))
                    {
                        JArray targets = availableForFinishConditions[j]["_props"]["target"] as JArray;
                        for (int k = 0; k< targets.Count; ++k)
                        {
                            if (questDataCompleteByItemTemplateID.TryGetValue(targets[k].ToString(), out QuestPair quests))
                            {
                                if (!quests.questData.ContainsKey(questData[i]["_id"].ToString()))
                                {
                                    quests.questData.Add(questData[i]["_id"].ToString(), questData[i]["QuestName"].ToString());
                                }
                                int.TryParse(availableForFinishConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                quests.count += parsedValue;
                            }
                            else
                            {
                                QuestPair newPair = new QuestPair();
                                newPair.questData.Add(questData[i]["_id"].ToString(), questData[i]["QuestName"].ToString());
                                int.TryParse(availableForFinishConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                newPair.count = parsedValue;
                                questDataCompleteByItemTemplateID.Add(targets[k].ToString(), newPair);
                            }

                            if (neededCompleteItemsByQuest.TryGetValue(questData[i]["_id"].ToString(), out Dictionary<string, int> items))
                            {
                                if (!items.ContainsKey(targets[k].ToString()))
                                {
                                    items.Add(targets[k].ToString(), 0);
                                }
                                int.TryParse(availableForFinishConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                items[targets[k].ToString()] += parsedValue;
                            }
                            else
                            {
                                Dictionary<string, int> newDict = new Dictionary<string, int>();
                                int.TryParse(availableForFinishConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                newDict.Add(targets[k].ToString(), parsedValue);
                                neededCompleteItemsByQuest.Add(questData[i]["_id"].ToString(), newDict);
                            }
                        }
                    }

                    if (availableForFinishConditions[j]["_parent"].ToString().Equals("PlaceBeacon"))
                    {
                        JArray targets = availableForFinishConditions[j]["_props"]["target"] as JArray;
                        for (int k = 0; k< targets.Count; ++k)
                        {
                            if (questDataCompleteByItemTemplateID.TryGetValue(targets[k].ToString(), out QuestPair quests))
                            {
                                if (!quests.questData.ContainsKey(questData[i]["_id"].ToString()))
                                {
                                    quests.questData.Add(questData[i]["_id"].ToString(), questData[i]["QuestName"].ToString());
                                }
                                int.TryParse(availableForFinishConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                quests.count += parsedValue;
                            }
                            else
                            {
                                QuestPair newPair = new QuestPair();
                                newPair.questData.Add(questData[i]["_id"].ToString(), questData[i]["QuestName"].ToString());
                                int.TryParse(availableForFinishConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                newPair.count = parsedValue;
                                questDataCompleteByItemTemplateID.Add(targets[k].ToString(), newPair);
                            }

                            if (neededCompleteItemsByQuest.TryGetValue(questData[i]["_id"].ToString(), out Dictionary<string, int> items))
                            {
                                if (!items.ContainsKey(targets[k].ToString()))
                                {
                                    items.Add(targets[k].ToString(), 0);
                                }
                                int.TryParse(availableForFinishConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                items[targets[k].ToString()] += parsedValue;
                            }
                            else
                            {
                                Dictionary<string, int> newDict = new Dictionary<string, int>();
                                int.TryParse(availableForFinishConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                newDict.Add(targets[k].ToString(), parsedValue);
                                neededCompleteItemsByQuest.Add(questData[i]["_id"].ToString(), newDict);
                            }
                        }
                    }
                }

                JArray availableForStartConditions = questData[i]["conditions"]["AvailableForStart"] as JArray;
                for(int j=0; j< availableForStartConditions.Count; ++j)
                {
                    if (availableForStartConditions[j]["_parent"].ToString().Equals("HandoverItem"))
                    {
                        JArray targets = availableForStartConditions[j]["_props"]["target"] as JArray;
                        for (int k = 0; k< targets.Count; ++k)
                        {
                            if (questDataStartByItemTemplateID.TryGetValue(targets[k].ToString(), out QuestPair quests))
                            {
                                if (!quests.questData.ContainsKey(questData[i]["_id"].ToString()))
                                {
                                    quests.questData.Add(questData[i]["_id"].ToString(), questData[i]["QuestName"].ToString());
                                }
                                int.TryParse(availableForStartConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                quests.count += parsedValue;
                            }
                            else
                            {
                                QuestPair newPair = new QuestPair();
                                newPair.questData.Add(questData[i]["_id"].ToString(), questData[i]["QuestName"].ToString());
                                int.TryParse(availableForStartConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                newPair.count = parsedValue;
                                questDataStartByItemTemplateID.Add(targets[k].ToString(), newPair);
                            }

                            if (neededStartItemsByQuest.TryGetValue(questData[i]["_id"].ToString(), out Dictionary<string, int> items))
                            {
                                if (!items.ContainsKey(targets[k].ToString()))
                                {
                                    items.Add(targets[k].ToString(), 0);
                                }
                                int.TryParse(availableForStartConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                items[targets[k].ToString()] += parsedValue;
                            }
                            else
                            {
                                Dictionary<string, int> newDict = new Dictionary<string, int>();
                                int.TryParse(availableForStartConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                newDict.Add(targets[k].ToString(), parsedValue);
                                neededStartItemsByQuest.Add(questData[i]["_id"].ToString(), newDict);
                            }
                        }
                    }

                    if (availableForStartConditions[j]["_parent"].ToString().Equals("FindItem"))
                    {
                        JArray targets = availableForStartConditions[j]["_props"]["target"] as JArray;
                        for (int k = 0; k< targets.Count; ++k)
                        {
                            // Check if there is a hand in item condition for the same item and at least the same count
                            // If so skip this, we will count the hand in instead
                            bool foundInHandin = false;
                            for (int l = 0; l < availableForStartConditions.Count; ++l)
                            {
                                if (availableForStartConditions[l]["_parent"].ToString().Equals("HandoverItem"))
                                {
                                    JArray handInTargets = availableForStartConditions[l]["_props"]["target"] as JArray;
                                    if (handInTargets != null && StringJArrayContainsString(handInTargets, targets[k].ToString()) &&
                                        (!int.TryParse(availableForStartConditions[l]["_props"]["value"].ToString(), out int parsedValue) ||
                                         !int.TryParse(availableForStartConditions[j]["_props"]["value"].ToString(), out int currentParsedValue) ||
                                         parsedValue == currentParsedValue))
                                    {
                                        foundInHandin = true;
                                        break;
                                    }
                                }
                            }
                            if (foundInHandin)
                            {
                                continue;
                            }

                            if (questDataStartByItemTemplateID.TryGetValue(targets[k].ToString(), out QuestPair quests))
                            {
                                if (!quests.questData.ContainsKey(questData[i]["_id"].ToString()))
                                {
                                    quests.questData.Add(questData[i]["_id"].ToString(), questData[i]["QuestName"].ToString());
                                }
                                int.TryParse(availableForStartConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                quests.count += parsedValue;
                            }
                            else
                            {
                                QuestPair newPair = new QuestPair();
                                newPair.questData.Add(questData[i]["_id"].ToString(), questData[i]["QuestName"].ToString());
                                int.TryParse(availableForStartConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                newPair.count = parsedValue;
                                questDataStartByItemTemplateID.Add(targets[k].ToString(), newPair);
                            }

                            if (neededStartItemsByQuest.TryGetValue(questData[i]["_id"].ToString(), out Dictionary<string, int> items))
                            {
                                if (!items.ContainsKey(targets[k].ToString()))
                                {
                                    items.Add(targets[k].ToString(), 0);
                                }
                                int.TryParse(availableForStartConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                items[targets[k].ToString()] += parsedValue;
                            }
                            else
                            {
                                Dictionary<string, int> newDict = new Dictionary<string, int>();
                                int.TryParse(availableForStartConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                newDict.Add(targets[k].ToString(), parsedValue);
                                neededStartItemsByQuest.Add(questData[i]["_id"].ToString(), newDict);
                            }
                        }
                    }

                    if (availableForStartConditions[j]["_parent"].ToString().Equals("LeaveItemAtLocation"))
                    {
                        JArray targets = availableForStartConditions[j]["_props"]["target"] as JArray;
                        for (int k = 0; k< targets.Count; ++k)
                        {
                            if (questDataStartByItemTemplateID.TryGetValue(targets[k].ToString(), out QuestPair quests))
                            {
                                if (!quests.questData.ContainsKey(questData[i]["_id"].ToString()))
                                {
                                    quests.questData.Add(questData[i]["_id"].ToString(), questData[i]["QuestName"].ToString());
                                }
                                int.TryParse(availableForStartConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                quests.count += parsedValue;
                            }
                            else
                            {
                                QuestPair newPair = new QuestPair();
                                newPair.questData.Add(questData[i]["_id"].ToString(), questData[i]["QuestName"].ToString());
                                int.TryParse(availableForStartConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                newPair.count = parsedValue;
                                questDataStartByItemTemplateID.Add(targets[k].ToString(), newPair);
                            }

                            if (neededStartItemsByQuest.TryGetValue(questData[i]["_id"].ToString(), out Dictionary<string, int> items))
                            {
                                if (!items.ContainsKey(targets[k].ToString()))
                                {
                                    items.Add(targets[k].ToString(), 0);
                                }
                                int.TryParse(availableForStartConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                items[targets[k].ToString()] += parsedValue;
                            }
                            else
                            {
                                Dictionary<string, int> newDict = new Dictionary<string, int>();
                                int.TryParse(availableForStartConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                newDict.Add(targets[k].ToString(), parsedValue);
                                neededStartItemsByQuest.Add(questData[i]["_id"].ToString(), newDict);
                            }
                        }
                    }

                    if (availableForStartConditions[j]["_parent"].ToString().Equals("PlaceBeacon"))
                    {
                        JArray targets = availableForStartConditions[j]["_props"]["target"] as JArray;
                        for (int k = 0; k< targets.Count; ++k)
                        {
                            if (questDataStartByItemTemplateID.TryGetValue(targets[k].ToString(), out QuestPair quests))
                            {
                                if (!quests.questData.ContainsKey(questData[i]["_id"].ToString()))
                                {
                                    quests.questData.Add(questData[i]["_id"].ToString(), questData[i]["QuestName"].ToString());
                                }
                                int.TryParse(availableForStartConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                quests.count += parsedValue;
                            }
                            else
                            {
                                QuestPair newPair = new QuestPair();
                                newPair.questData.Add(questData[i]["_id"].ToString(), questData[i]["QuestName"].ToString());
                                int.TryParse(availableForStartConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                newPair.count = parsedValue;
                                questDataStartByItemTemplateID.Add(targets[k].ToString(), newPair);
                            }

                            if (neededStartItemsByQuest.TryGetValue(questData[i]["_id"].ToString(), out Dictionary<string, int> items))
                            {
                                if (!items.ContainsKey(targets[k].ToString()))
                                {
                                    items.Add(targets[k].ToString(), 0);
                                }
                                int.TryParse(availableForStartConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                items[targets[k].ToString()] += parsedValue;
                            }
                            else
                            {
                                Dictionary<string, int> newDict = new Dictionary<string, int>();
                                int.TryParse(availableForStartConditions[j]["_props"]["value"].ToString(), out int parsedValue);
                                newDict.Add(targets[k].ToString(), parsedValue);
                                neededStartItemsByQuest.Add(questData[i]["_id"].ToString(), newDict);
                            }
                        }
                    }
                }
            }
        }

        private bool StringJArrayContainsString(JArray arr, string s)
        {
            for(int i=0; i < arr.Count; ++i)
            {
                if (arr[i].ToString().Equals(s))
                {
                    return true;
                }
            }
            return false;
        }

        private void LoadConfig()
        {
            try
            {
                config = JObject.Parse(File.ReadAllText(modPath + "/Config.json"));

                if (config["fulfilledAnyCanBeUpgraded"] != null)
                {
                    fulfilledAnyCanBeUpgraded = (bool)config["fulfilledAnyCanBeUpgraded"];
                }
                if (config["questPriority"] != null)
                {
                    questPriority = (int)config["questPriority"];
                }
                if (config["hideoutPriority"] != null)
                {
                    hideoutPriority = (int)config["hideoutPriority"];
                }
                if (config["wishlistPriority"] != null)
                {
                    wishlistPriority = (int)config["wishlistPriority"];
                }
                if (config["showLockedModules"] != null)
                {
                    showLockedModules = (bool)config["showLockedModules"];
                }
                if (config["needMoreColor"] != null)
                {
                    needMoreColor = new Color((float)config["needMoreColor"][0], (float)config["needMoreColor"][1], (float)config["needMoreColor"][2]);
                }
                if (config["fulfilledColor"] != null)
                {
                    fulfilledColor = new Color((float)config["fulfilledColor"][0], (float)config["fulfilledColor"][1], (float)config["fulfilledColor"][2]);
                }
                if (config["wishlistColor"] != null)
                {
                    wishlistColor = new Color((float)config["wishlistColor"][0], (float)config["wishlistColor"][1], (float)config["wishlistColor"][2]);
                }
                if (config["includeFutureQuests"] != null)
                {
                    includeFutureQuests = (bool)config["includeFutureQuests"];
                }

                Logger.LogInfo("Configs loaded");
            }
            catch (FileNotFoundException) { /* In case of file not found, we don't want to do anything, user prob deleted it for a reason */ }
            catch (Exception ex) { MoreCheckmarksMod.LogError("Couldn't read MoreCheckmarksConfig.txt, using default settings instead. Error: " + ex.Message); }
        }

        private void LoadAssets()
        {
            AssetBundle assetBundle = AssetBundle.LoadFromFile(modPath+"/MoreCheckmarksAssets");

            if(assetBundle == null)
            {
                MoreCheckmarksMod.LogError("Failed to load assets, inspect window checkmark may be miscolored");
            }
            else
            {
                whiteCheckmark = assetBundle.LoadAsset<Sprite>("WhiteCheckmark");

                benderBold = assetBundle.LoadAsset<TMP_FontAsset>("BenderBold");
                TMP_Text.OnFontAssetRequest += TMP_Text_onFontAssetRequest;

                MoreCheckmarksMod.LogInfo("Assets loaded");
            }
        }

        public static TMP_FontAsset TMP_Text_onFontAssetRequest(int hash, string name)
        {
            if (name.Equals("BENDERBOLD"))
            {
                return benderBold;
            }
            else
            {
                return null;
            }
        }

        public static void DoPatching()
        {
            // Get assemblies
            Type ProfileSelector = null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for(int i=0; i < assemblies.Length; ++i)
            {
                if (assemblies[i].GetName().Name.Equals("Assembly-CSharp"))
                {
                    ProfileSelector = assemblies[i].GetType("Class225").GetNestedType("Class1218", BindingFlags.NonPublic);
                }
            }

            var harmony = new HarmonyLib.Harmony("VIP.TommySoucy.MoreCheckmarks");

            // Auto patch
            harmony.PatchAll();

            // Manual patch
            MethodInfo profileSelectorOriginal = ProfileSelector.GetMethod("method_0", BindingFlags.NonPublic | BindingFlags.Instance);
            MethodInfo profileSelectorPostfix = typeof(ProfileSelectionPatch).GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static);

            harmony.Patch(profileSelectorOriginal, null, new HarmonyMethod(profileSelectorPostfix));
        }

        public static Color ParseColor(string colorString)
        {
            string trimmed = colorString.Trim(new char[] { '(', ')' });
            string[] values = trimmed.Split(',');

            return new Color(float.Parse(values[0]),float.Parse(values[1]),float.Parse(values[2]));
        }

        public static NeededStruct GetNeeded(string itemTemplateID, ref List<string> areaNames)
        {
            NeededStruct neededStruct = new NeededStruct();
            neededStruct.possessedCount = 0;
            neededStruct.requiredCount = 0;

            try
            {
                HideoutClass hideoutInstance = Comfort.Common.Singleton<HideoutClass>.Instance;
                foreach (EFT.Hideout.AreaData ad in hideoutInstance.AreaDatas)
                {
                    if(ad == null || ad.Template == null || ad.Template.Name == null || ad.NextStage == null)
                    {
                        continue;
                    }

                    if (ad.Template.Name.Equals("Place of fame"))
                    {
                        continue;
                    }

                    EFT.Hideout.Stage actualNextStage = ad.NextStage;

                    // If we don't want to get requirement of locked to construct areas, skip if it is locked to construct
                    if (!MoreCheckmarksMod.showLockedModules && ad.Status == EFT.Hideout.EAreaStatus.LockedToConstruct)
                    {
                        continue;
                    }

                    // If the area has no future upgrade, skip
                    if (ad.Status == EFT.Hideout.EAreaStatus.NoFutureUpgrades)
                    {
                        continue;
                    }

                    // If in process of constructing or upgrading, go to actual next stage if it exists
                    if (ad.Status == EFT.Hideout.EAreaStatus.Constructing ||
                       ad.Status == EFT.Hideout.EAreaStatus.Upgrading)
                    {
                        actualNextStage = ad.StageAt(ad.NextStage.Level + 1);

                        // If there are not StageAt given level, it will return a new stage, so level will be 0
                        if (actualNextStage == null || actualNextStage.Level == 0)
                        {
                            continue;
                        }
                    }

                    EFT.Hideout.RelatedRequirements requirements = actualNextStage.Requirements;

                    try
                    {
                        foreach (var requirement in requirements)
                        {
                            if (requirement != null)
                            {
                                EFT.Hideout.ItemRequirement itemRequirement = requirement as EFT.Hideout.ItemRequirement;
                                if (itemRequirement != null)
                                {
                                    string requirementTemplate = itemRequirement.TemplateId;
                                    if (itemTemplateID == requirementTemplate)
                                    {
                                        // Sum up the total amount of this item required in entire hideout and update possessed amount
                                        neededStruct.requiredCount += itemRequirement.IntCount;
                                        neededStruct.possessedCount = itemRequirement.UserItemsCount;

                                        // A requirement but already have the amount we need
                                        if (requirement.Fulfilled)
                                        {
                                            // Even if we have enough of this item to fulfill a requirement in one area
                                            // we might still need it, and if thats the case we want to show that color, not fulfilled color, so you know you still need more of it
                                            // So only set color to fulfilled if not needed
                                            if (!neededStruct.foundNeeded && !neededStruct.foundFulfilled)
                                            {
                                                neededStruct.foundFulfilled = true;
                                            }

                                            if (areaNames != null)
                                            {
                                                areaNames.Add("<color=#" + ColorUtility.ToHtmlStringRGB(MoreCheckmarksMod.fulfilledColor) + ">" + ad.Template.Name + "</color>");
                                            }
                                        }
                                        else
                                        {
                                            if (!neededStruct.foundNeeded)
                                            {
                                                neededStruct.foundNeeded = true;
                                            }

                                            if (areaNames != null)
                                            {
                                                areaNames.Add("<color=#" + ColorUtility.ToHtmlStringRGB(MoreCheckmarksMod.needMoreColor) + ">" + ad.Template.Name + "</color>");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        MoreCheckmarksMod.LogError("Failed to get whether item " + itemTemplateID + " was needed for hideout area: "+ ad.Template.Name);
                    }
                }
            }
            catch (Exception)
            {
                MoreCheckmarksMod.LogError("Failed to get whether item "+itemTemplateID+" was needed for hideout upgrades.");
            }

            return neededStruct;
        }

        public static bool IsQuestItem(IEnumerable<QuestDataClass> quests, string templateID)
        {
            //QuestControllerClass.GetItemsForCondition
            try
            {
                if (includeFutureQuests)
                {
                    return questDataCompleteByItemTemplateID.TryGetValue(templateID, out QuestPair questPair) && questPair.questData.Count > 0;
                }
                else
                {
                    foreach (QuestDataClass quest in quests)
                    {
                        if (quest != null &&
                            quest.Status == EQuestStatus.Started &&
                            quest.Template != null && quest.Template.Conditions != null && quest.Template.Conditions.ContainsKey(EQuestStatus.AvailableForFinish))
                        {
                            IEnumerable<ConditionItem> conditions = quest.Template.GetConditions<ConditionItem>(EQuestStatus.AvailableForFinish);
                            if (conditions != null)
                            {
                                foreach (ConditionItem condition in conditions)
                                {
                                    if (condition != null && condition.target != null && condition.target.Contains(templateID))
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                MoreCheckmarksMod.LogError("Failed to get whether item " + templateID + " is quest item: " + ex.Message + "\n" + ex.StackTrace);
            }

            return false;
        }

        public static void LogInfo(string msg)
        {
            modInstance.Logger.LogInfo(msg);
        }

        public static void LogError(string msg)
        {
            modInstance.Logger.LogError(msg);
        }
    }

    [HarmonyPatch]
    class QuestItemViewPanelShowPatch
    {
        // This postfix essentially overrides the sprite and its color after it has been set by Show()
        // Just to make it different in case it is a hideout requirement
        [HarmonyPatch(typeof(EFT.UI.DragAndDrop.QuestItemViewPanel), nameof(EFT.UI.DragAndDrop.QuestItemViewPanel.Show))]
        static void Postfix(EFT.Profile profile, EFT.InventoryLogic.Item item, EFT.UI.SimpleTooltip tooltip, EFT.UI.DragAndDrop.QuestItemViewPanel __instance,
                            ref Image ____questIconImage, ref Sprite ____foundInRaidSprite, ref string ___string_5, ref EFT.UI.SimpleTooltip ___simpleTooltip_0,
                            TextMeshProUGUI ____questItemLabel)
        {
            List<string> areaNames = new List<string>();
            bool questItem = item.QuestItem || (___string_5 != null && ___string_5.Contains("quest"));

            if (____questItemLabel != null)
            {
                // Since being quest item could be set by future quests, need to make sure we have "QUEST ITEM" label
                if (questItem)
                {
                    ____questItemLabel.text = "QUEST ITEM";
                }
                else // Not quest item but the label string will be there by default in inspect panel so will show if enabled, so need to make sure we remove it
                {
                    ____questItemLabel.text = "";
                }
                ____questItemLabel.gameObject.SetActive(questItem);
            }

            NeededStruct neededStruct = MoreCheckmarksMod.GetNeeded(item.TemplateId, ref areaNames);

            bool wishlist = false;
            try
            {
                wishlist = ItemUiContext.Instance.IsInWishList(item.TemplateId);
            }catch{ }

            try
            {
                if (neededStruct.foundNeeded)
                {
                    if (wishlist && MoreCheckmarksMod.wishlistPriority > MoreCheckmarksMod.hideoutPriority)
                    {
                        SetCheckmark(profile, item.TemplateId, questItem, __instance, ____questIconImage, ____foundInRaidSprite, MoreCheckmarksMod.wishlistColor, true);
                    }
                    else
                    {
                        SetCheckmark(profile, item.TemplateId, questItem, __instance, ____questIconImage, ____foundInRaidSprite, MoreCheckmarksMod.needMoreColor, false);
                    }

                    SetTooltip(areaNames, ref ___string_5, ref ___simpleTooltip_0, ref tooltip, item, questItem, wishlist, neededStruct.possessedCount, neededStruct.requiredCount);
                }
                else if (neededStruct.foundFulfilled)
                {
                    if (wishlist && MoreCheckmarksMod.wishlistPriority > MoreCheckmarksMod.hideoutPriority)
                    {
                        SetCheckmark(profile, item.TemplateId, questItem, __instance, ____questIconImage, ____foundInRaidSprite, MoreCheckmarksMod.wishlistColor, true);
                    }
                    else
                    {
                        if (MoreCheckmarksMod.fulfilledAnyCanBeUpgraded)
                        {
                            SetCheckmark(profile, item.TemplateId, questItem, __instance, ____questIconImage, ____foundInRaidSprite, MoreCheckmarksMod.fulfilledColor, false);
                        }
                        else // We only want blue checkmark when ALL requiring this item can be upgraded (if all other requirements are fulfilled too but thats implied)
                        {
                            // Check if we trully do not need more of this item for now
                            if (neededStruct.possessedCount >= neededStruct.requiredCount)
                            {
                                SetCheckmark(profile, item.TemplateId, questItem, __instance, ____questIconImage, ____foundInRaidSprite, MoreCheckmarksMod.fulfilledColor, false);
                            }
                            else // Still need more
                            {
                                SetCheckmark(profile, item.TemplateId, questItem, __instance, ____questIconImage, ____foundInRaidSprite, MoreCheckmarksMod.needMoreColor, false);
                            }
                        }
                    }

                    SetTooltip(areaNames, ref ___string_5, ref ___simpleTooltip_0, ref tooltip, item, questItem, wishlist, neededStruct.possessedCount, neededStruct.requiredCount);
                }
                else if (wishlist) // We don't want to color it for hideout, but it is in wishlist
                {
                    SetTooltip(areaNames, ref ___string_5, ref ___simpleTooltip_0, ref tooltip, item, questItem, true, neededStruct.possessedCount, neededStruct.requiredCount);

                    SetCheckmark(profile, item.TemplateId, questItem, __instance, ____questIconImage, ____foundInRaidSprite, MoreCheckmarksMod.wishlistColor, true);
                }
                else
                {
                    // Just to make sure the change is not permanent, because the color is never set back to the default white by EFT
                    // Because if an item was a requirement, its sprite's color set to green/blue, then it stopped being a requirement, but it was found in raid/is quest item
                    // the sprite would still show up green/blue
                    ____questIconImage.color = Color.white;

                    MoreCheckmarksMod.setColor = false;
                }
            }
            catch
            {
                if(item != null)
                {
                    MoreCheckmarksMod.LogError("QuestItemViewPanelShowPatch postfix failed on item: " + item.TemplateId + " named " + item.LocalizedName());
                }
                else
                {
                    MoreCheckmarksMod.LogError("QuestItemViewPanelShowPatch postfix failed, item null");
                }
            }
        }

        private static void SetCheckmark(EFT.Profile profile, string templateID, bool questItem, EFT.UI.DragAndDrop.QuestItemViewPanel __instance, Image ____questIconImage,
                                         Sprite sprite, Color color, bool wishlist)
        {
            try
            {
                // At this point we got the color that was prioritized between wishlist and hideout, now have to compare with quest
                // Set checkmark depending on priority settings
                if (!questItem || MoreCheckmarksMod.questPriority < (wishlist ? MoreCheckmarksMod.wishlistPriority : MoreCheckmarksMod.hideoutPriority))
                {
                    // Following calls base class method ShowGameObject()
                    __instance.ShowGameObject();
                    ____questIconImage.sprite = sprite;
                    ____questIconImage.color = color;

                    MoreCheckmarksMod.setColor = true;
                }
                else
                {
                    MoreCheckmarksMod.setColor = false;
                }
            }
            catch
            {
                MoreCheckmarksMod.LogError("SetCheckmark failed");
            }
        }

        private static void SetTooltip(List<string> areaNames, ref string ___string_3, ref EFT.UI.SimpleTooltip ___simpleTooltip_0, ref EFT.UI.SimpleTooltip tooltip,
                                       EFT.InventoryLogic.Item item, bool questItem, bool wishlist, int possessedCount, int requiredCount)
        {
            try
            {
                // Build string of list of areas this is needed for
                string areaNamesString = "";
                for (int i = 0; i < areaNames.Count; ++i)
                {
                    areaNamesString += (i == 0 ? "" : (areaNames.Count == 2 ? "" : ",") + (i == areaNames.Count - 1 ? " and " : " ")) + areaNames[i];
                }

                if (!areaNamesString.Equals(""))
                {
                    if (___string_3 != null && (item.MarkedAsSpawnedInSession || questItem))
                    {
                        ___string_3 += string.Format(", and needed for {0} ({1}/{2})".Localized(), areaNamesString, possessedCount, requiredCount);

                        if (wishlist)
                        {
                            ___string_3 += string.Format(", and on {0}".Localized(), "<color=#" + ColorUtility.ToHtmlStringRGB(MoreCheckmarksMod.wishlistColor) + ">Wish List</color>");
                        }
                    }
                    else
                    {
                        ___string_3 = string.Format("Needed for {0} ({1}/{2})".Localized(), areaNamesString, possessedCount, requiredCount);

                        if (wishlist)
                        {
                            ___string_3 += string.Format(", and on {0}".Localized(), "<color=#" + ColorUtility.ToHtmlStringRGB(MoreCheckmarksMod.wishlistColor) + ">Wish List</color>");
                        }
                    }
                }
                else // Means the method was called for wishlist
                {
                    if (___string_3 != null && (item.MarkedAsSpawnedInSession || questItem))
                    {
                        ___string_3 += string.Format(", and on {0}".Localized(), "<color=#" + ColorUtility.ToHtmlStringRGB(MoreCheckmarksMod.wishlistColor) + ">Wish List</color>");
                    }
                    else
                    {
                        ___string_3 = string.Format("On {0}".Localized(), "<color=#" + ColorUtility.ToHtmlStringRGB(MoreCheckmarksMod.wishlistColor) + ">Wish List</color>");
                    }
                }

                // If this is not a quest item or found in raid, the original returns and the tooltip never gets set, so we need to set it ourselves
                ___simpleTooltip_0 = tooltip;
            }
            catch
            {
                MoreCheckmarksMod.LogError("SetToolTip failed");
            }
        }
    }

    [HarmonyPatch]
    class ItemSpecificationPanelShowPatch
    {
        // This postfix will run after the inspect window sets its checkmark if there is one
        // If there is one, the postfix for the QuestItemViewPanel will always have run before
        // This patch just changes the sprite to a default white one if needed, so we can set its color to whatever we need
        [HarmonyPatch(typeof(EFT.UI.ItemSpecificationPanel), "method_2")]
        static void Postfix(ref Item ___item_0, ref QuestItemViewPanel ____questItemViewPanel)
        {
            try
            {
                // If the checkmark exists and if the color of the checkmark is custom
                if (____questItemViewPanel != null && MoreCheckmarksMod.setColor)
                {
                    // Get access to QuestItemViewPanel's private _questIconImage
                    BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                    FieldInfo iconImageField = typeof(QuestItemViewPanel).GetField("_questIconImage", bindFlags);
                    Image _questIconImage = iconImageField.GetValue(____questItemViewPanel) as Image;

                    if (_questIconImage != null)
                    {
                        _questIconImage.sprite = MoreCheckmarksMod.whiteCheckmark;
                    }
                }
            }
            catch
            {
                MoreCheckmarksMod.LogError("ItemSpecificationPanelShowPatch failed");
            }
        }
    }

    [HarmonyPatch]
    class QuestItemViewPanelQuestTooltipPatch
    {
        // This postfix will replace quest status tooltip of the item view to include more quests if necessary
        [HarmonyPatch(typeof(QuestItemViewPanel), "method_0")]
        static void Postfix(Profile profile, Item item, ref string ___string_5, ref object __result)
        {
            try
            {
                if (MoreCheckmarksMod.includeFutureQuests)
                {
                    int possessedCount = 0;
                    if (profile != null)
                    {
                        IEnumerable<Item> inventoryItems = profile.Inventory.GetAllItemByTemplate(item.TemplateId);
                        if (inventoryItems != null)
                        {
                            foreach (Item currentItem in inventoryItems)
                            {
                                if (currentItem.SpawnedInSession)
                                {
                                    possessedCount += currentItem.StackObjectsCount;
                                }
                            }
                        }
                    }
                    else
                    {
                        MoreCheckmarksMod.LogError("Profile null for item " + item.Template.Name);
                    }

                    string questStartString = "<color=#dd831a>";
                    bool gotStartQuests = false;
                    bool gotMoreThanOneStartQuest = false;
                    int totalItemCount = 0;
                    if (MoreCheckmarksMod.questDataStartByItemTemplateID.TryGetValue(item.TemplateId, out MoreCheckmarksMod.QuestPair startQuests))
                    {
                        if (startQuests.questData.Count > 0)
                        {
                            gotStartQuests = true;
                            totalItemCount = startQuests.count;
                        }
                        if (startQuests.questData.Count > 1)
                        {
                            gotMoreThanOneStartQuest = true;
                        }
                        int count = startQuests.questData.Count;
                        int index = 0;
                        foreach (KeyValuePair<string, string> questEntry in startQuests.questData)
                        {
                            questStartString += questEntry.Value;
                            if (index != count - 1)
                            {
                                questStartString += ", ";
                                if (index == count - 2)
                                {
                                    questStartString += "</color>and <color=#dd831a>";
                                }
                            }
                            else
                            {
                                questStartString += "</color>";
                            }

                            ++index;
                        }
                    }
                    if (gotStartQuests)
                    {
                        ___string_5 = "Item will be/is needed to start quest" + (gotMoreThanOneStartQuest ? "s" : "") + " " + questStartString + " (" + possessedCount + "/" + totalItemCount + ")";
                    }
                    string questCompleteString = "<color=#dd831a>";
                    bool gotCompleteQuests = false;
                    bool gotMoreThanOneCompleteQuest = false;
                    if (MoreCheckmarksMod.questDataCompleteByItemTemplateID.TryGetValue(item.TemplateId, out MoreCheckmarksMod.QuestPair completeQuests))
                    {
                        if (completeQuests.questData.Count > 0)
                        {
                            gotCompleteQuests = true;
                            totalItemCount = completeQuests.count;
                        }
                        if (completeQuests.questData.Count > 1)
                        {
                            gotMoreThanOneCompleteQuest = true;
                        }
                        int count = completeQuests.questData.Count;
                        int index = 0;
                        foreach (KeyValuePair<string, string> questEntry in completeQuests.questData)
                        {
                            questCompleteString += questEntry.Value;
                            if (index != count - 1)
                            {
                                questCompleteString += ", ";
                                if (index == count - 2)
                                {
                                    questCompleteString += "</color>and <color=#dd831a>";
                                }
                            }
                            else
                            {
                                questCompleteString += "</color>";
                            }

                            ++index;
                        }
                    }
                    if (gotCompleteQuests)
                    {
                        if (gotStartQuests)
                        {
                            ___string_5 += ", and will be/is needed to complete quest" + (gotMoreThanOneCompleteQuest ? "s" : "") + " " + questCompleteString + " (" + possessedCount + "/" + totalItemCount + ")";
                        }
                        else
                        {
                            ___string_5 = "Item will be/is needed to complete quest" + (gotMoreThanOneCompleteQuest ? "s" : "") + " " + questCompleteString + " (" + possessedCount + "/" + totalItemCount + ")";
                        }
                    }

                    if (gotStartQuests || gotCompleteQuests)
                    {
                        __result = 2;
                    }
                }
            }
            catch(Exception ex)
            {
                MoreCheckmarksMod.LogError("QuestItemViewPanelQuestTooltipPatch failed: "+ex.Message+"\n"+ex.StackTrace);
            }
        }
    }

    [HarmonyPatch]
    class AvailableActionsPatch
    {
        // This postfix will run after we get a list of all actions available to interact with the item we are pointing at
        [HarmonyPatch(typeof(InteractionController), "smethod_3")]
        static void Postfix(GamePlayerOwner owner, LootItem lootItem, ref InteractionInstance __result)
        {
            try
            {
                foreach (Action action in __result.Actions)
                {
                    if (action.Name.Equals("Take"))
                    {
                        List<string> nullAreaNames = null;
                        NeededStruct neededStruct = MoreCheckmarksMod.GetNeeded(lootItem.TemplateId, ref nullAreaNames);
                        bool wishlist = ItemUiContext.Instance.IsInWishList(lootItem.TemplateId);
                        bool questItem = MoreCheckmarksMod.IsQuestItem(owner.Player.Profile.QuestsData, lootItem.TemplateId);

                        if (neededStruct.foundNeeded)
                        {
                            if (wishlist && MoreCheckmarksMod.wishlistPriority > MoreCheckmarksMod.hideoutPriority)
                            {
                                if (questItem && MoreCheckmarksMod.questPriority > MoreCheckmarksMod.wishlistPriority)
                                {
                                    action.Name = "<font=\"BenderBold\"><color=#FFE433>Take</color></font>";
                                }
                                else
                                {
                                    action.Name = "<font=\"BenderBold\"><color=#" + ColorUtility.ToHtmlStringRGB(MoreCheckmarksMod.wishlistColor) + ">Take</color></font>";
                                }

                            }
                            else
                            {
                                if (questItem && MoreCheckmarksMod.questPriority > MoreCheckmarksMod.hideoutPriority)
                                {
                                    action.Name = "<font=\"BenderBold\"><color=#FFE433>Take</color></font>";
                                }
                                else
                                {
                                    action.Name = "<font=\"BenderBold\"><color=#" + ColorUtility.ToHtmlStringRGB(MoreCheckmarksMod.needMoreColor) + ">Take</color></font>";
                                }
                            }
                        }
                        else if (neededStruct.foundFulfilled)
                        {
                            if (wishlist && MoreCheckmarksMod.wishlistPriority > MoreCheckmarksMod.hideoutPriority)
                            {
                                if (questItem && MoreCheckmarksMod.questPriority > MoreCheckmarksMod.wishlistPriority)
                                {
                                    action.Name = "<font=\"BenderBold\"><color=#FFE433>Take</color></font>";
                                }
                                else
                                {
                                    action.Name = "<font=\"BenderBold\"><color=#" + ColorUtility.ToHtmlStringRGB(MoreCheckmarksMod.wishlistColor) + ">Take</color></font>";
                                }
                            }
                            else
                            {
                                if (MoreCheckmarksMod.fulfilledAnyCanBeUpgraded)
                                {
                                    if (questItem && MoreCheckmarksMod.questPriority > MoreCheckmarksMod.hideoutPriority)
                                    {
                                        action.Name = "<font=\"BenderBold\"><color=#FFE433>Take</color></font>";
                                    }
                                    else
                                    {
                                        action.Name = "<font=\"BenderBold\"><color=#" + ColorUtility.ToHtmlStringRGB(MoreCheckmarksMod.fulfilledColor) + ">Take</color></font>";
                                    }
                                }
                                else // We only want blue checkmark when ALL requiring this item can be upgraded (if all other requirements are fulfilled too but thats implied)
                                {
                                    // Check if we trully do not need more of this item for now
                                    if (neededStruct.possessedCount >= neededStruct.requiredCount)
                                    {
                                        if (questItem && MoreCheckmarksMod.questPriority > MoreCheckmarksMod.hideoutPriority)
                                        {
                                            action.Name = "<font=\"BenderBold\"><color=#FFE433>Take</color></font>";
                                        }
                                        else
                                        {
                                            action.Name = "<font=\"BenderBold\"><color=#" + ColorUtility.ToHtmlStringRGB(MoreCheckmarksMod.fulfilledColor) + ">Take</color></font>";
                                        }
                                    }
                                    else // Still need more
                                    {
                                        if (questItem && MoreCheckmarksMod.questPriority > MoreCheckmarksMod.hideoutPriority)
                                        {
                                            action.Name = "<font=\"BenderBold\"><color=#FFE433>Take</color></font>";
                                        }
                                        else
                                        {
                                            action.Name = "<font=\"BenderBold\"><color=#" + ColorUtility.ToHtmlStringRGB(MoreCheckmarksMod.needMoreColor) + ">Take</color></font>";
                                        }
                                    }
                                }
                            }
                        }
                        else if (wishlist) // We don't want to color it for hideout, but it is in wishlist
                        {
                            if (questItem && MoreCheckmarksMod.questPriority > MoreCheckmarksMod.wishlistPriority)
                            {
                                action.Name = "<font=\"BenderBold\"><color=#FFE433>Take</color></font>";
                            }
                            else
                            {
                                action.Name = "<font=\"BenderBold\"><color=#" + ColorUtility.ToHtmlStringRGB(MoreCheckmarksMod.wishlistColor) + ">Take</color></font>";
                            }
                        }
                        else if (questItem) // We don't want to color it for anything but it is a quest item
                        {
                            action.Name = "<font=\"BenderBold\"><color=#FFE433>Take</color></font>";
                        }
                        //else leave it as it is

                        break;
                    }
                }
            }
            catch(Exception ex)
            {
                MoreCheckmarksMod.LogError("Failed to process available actions for loose item: "+ex.Message+"\n"+ex.StackTrace);
            }
        }
    }

    [HarmonyPatch]
    class QuestClassStatusPatch
    {
        private static EQuestStatus preStatus;

        // This prefix will run before a quest's status has been set 
        [HarmonyPatch(typeof(QuestClass), "set_QuestStatus")]
        static void Prefix(QuestClass __instance)
        {
            preStatus = __instance.QuestStatus;
        }

        // This postfix will run after a quest's status has been set 
        [HarmonyPatch(typeof(QuestClass), "set_QuestStatus")]
        static void Postfix(QuestClass __instance)
        {
            if(__instance == null)
            {
                MoreCheckmarksMod.LogError("Attempted setting queststatus but instance is null");
                return;
            }
            if(__instance.Template == null)
            {
                return;
            }

            MoreCheckmarksMod.LogInfo("Quest "+__instance.Template.Name+ " queststatus set to "+ __instance.QuestStatus);

            try
            {
                if (__instance.QuestStatus != preStatus)
                {
                    switch (__instance.QuestStatus)
                    {
                        case EQuestStatus.Started:
                            if (preStatus == EQuestStatus.AvailableForStart)
                            {
                                if (MoreCheckmarksMod.neededStartItemsByQuest.TryGetValue(__instance.Template.Id, out Dictionary<string, int> startItems))
                                {
                                    foreach (KeyValuePair<string, int> itemEntry in startItems)
                                    {
                                        if (MoreCheckmarksMod.questDataStartByItemTemplateID.TryGetValue(itemEntry.Key, out MoreCheckmarksMod.QuestPair questList))
                                        {
                                            questList.questData.Remove(__instance.Template.Id);
                                            questList.count -= itemEntry.Value;
                                            if (questList.questData.Count == 0)
                                            {
                                                MoreCheckmarksMod.questDataStartByItemTemplateID.Remove(itemEntry.Key);
                                            }
                                        }
                                    }

                                    MoreCheckmarksMod.neededStartItemsByQuest.Remove(__instance.Template.Id);
                                }
                            }
                            break;
                        case EQuestStatus.Success:
                        case EQuestStatus.Expired:
                        case EQuestStatus.Fail:
                            if (MoreCheckmarksMod.neededCompleteItemsByQuest.TryGetValue(__instance.Template.Id, out Dictionary<string, int> completeItems))
                            {
                                foreach (KeyValuePair<string, int> itemEntry in completeItems)
                                {
                                    if (MoreCheckmarksMod.questDataCompleteByItemTemplateID.TryGetValue(itemEntry.Key, out MoreCheckmarksMod.QuestPair questList))
                                    {
                                        questList.questData.Remove(__instance.Template.Id);
                                        questList.count -= itemEntry.Value;
                                        if (questList.questData.Count == 0)
                                        {
                                            MoreCheckmarksMod.questDataCompleteByItemTemplateID.Remove(itemEntry.Key);
                                        }
                                    }
                                }

                                MoreCheckmarksMod.neededCompleteItemsByQuest.Remove(__instance.Template.Id);
                            }
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                MoreCheckmarksMod.LogError("Failed to process change in status for quest " + __instance.Template.Name + " to " + __instance.QuestStatus + ": " + ex.Message + "\n" + ex.StackTrace);
            }
        }
    }

    class ProfileSelectionPatch
    {
        // This prefix will run right after a profile has been selected
        static void Postfix()
        {
            MoreCheckmarksMod.modInstance.LoadData();
        }
    }
}
