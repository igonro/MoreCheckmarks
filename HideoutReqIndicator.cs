﻿using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using HarmonyLib;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using Newtonsoft.Json.Linq;
using System;

using Requirement = GClass1278; // EFT.Hideout.RelatedRequirements as Data field (list)
using HideoutInstance = GClass1251; // search for AreaDatas (Member)

namespace HideoutRequirementIndicator
{

    public class HideoutRequirementIndicatorMod : MelonMod
    {
        public static bool blueAnyCanBeUpgraded = false;
        public static bool prioritizeQuest = false;
        public static bool showLockedModules = true;

        public override void OnApplicationStart()
        {
            Init();

            DoPatching();
        }

        private static void Init()
        {
            ServicePointManager.ServerCertificateValidationCallback +=
            delegate (
                object sender,
                X509Certificate certificate,
                X509Chain chain,
                SslPolicyErrors sslPolicyErrors)
            {
                return true;
            };

            HttpClient client = new HttpClient();
            client.BaseAddress = new System.Uri("https://127.0.0.1:443/");
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            HttpResponseMessage response = client.GetAsync("server/config/checkmarksfail").Result;

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    JObject o = JObject.Parse(response.Content.ReadAsStringAsync().Result);

                    blueAnyCanBeUpgraded = bool.Parse(o["blueAnyCanBeUpgraded"].ToString());
                    prioritizeQuest = bool.Parse(o["prioritizeQuest"].ToString());
                    showLockedModules = bool.Parse(o["showLockedModules"].ToString());
                }
                catch
                {
                    // If route doesn't exist on server, the response status will be success but json will throw exception on parse, so load local config instead
                    LoadLocalConfig();
                }
            }
            else
            {
                // If for any reason response is not success, load local config instead
                LoadLocalConfig();
            }

            client.Dispose();
        }

        private static void LoadLocalConfig()
        {
            try
            {
                string[] lines = File.ReadAllLines("Mods/HideoutRequirementIndicatorConfig.txt");

                foreach (string line in lines)
                {
                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }

                    string trimmedLine = line.Trim();
                    string[] tokens = trimmedLine.Split(' ');

                    if (tokens.Length == 0)
                    {
                        continue;
                    }

                    if (tokens[0].Equals("blueAnyCanBeUpgraded"))
                    {
                        if (trimmedLine.IndexOf("true") > -1)
                        {
                            blueAnyCanBeUpgraded = true;
                        }
                    }
                    else if (tokens[0].Equals("prioritizeQuest"))
                    {
                        if (trimmedLine.IndexOf("true") > -1)
                        {
                            prioritizeQuest = true;
                        }
                    }
                    else if (tokens[0].Equals("showLockedModules"))
                    {
                        if (trimmedLine.IndexOf("false") > -1)
                        {
                            showLockedModules = false;
                        }
                    }
                }
            }
            catch(FileNotFoundException) { /* In case of file not found, we don't want to do anything, user prob deleted it for a reason */ }
            catch(Exception ex) { MelonLogger.Msg("Couldn't read HideoutRequirementIndicatorConfig.txt, using default settings instead. Error: "+ex.Message); }
        }

        public static void DoPatching()
        {
            var harmony = new HarmonyLib.Harmony("VIP.TommySoucy.HideoutRequirementIndicator");

            harmony.PatchAll();
        }
    }

    [HarmonyPatch]
    class ShowPatch
    {
        // This postfix essentially overrides the sprite and its color after it has been set by Show()
        // Just to make it different in case it is a hideout requirement
        [HarmonyPatch(typeof(EFT.UI.DragAndDrop.QuestItemViewPanel), nameof(EFT.UI.DragAndDrop.QuestItemViewPanel.Show))]
        static void Postfix(EFT.Profile profile, EFT.InventoryLogic.Item item, EFT.UI.SimpleTooltip tooltip, EFT.UI.DragAndDrop.QuestItemViewPanel __instance,
                            ref Image ____questIconImage, ref Sprite ____foundInRaidSprite, ref string ___string_3, ref EFT.UI.SimpleTooltip ___simpleTooltip_0)
        {
            string template = item.TemplateId;
            bool foundNeeded = false;
            bool foundFullfilled = false;
            List<string> areaNames = new List<string>();
            bool questItem = item.QuestItem || (___string_3 != null && ___string_3.Contains("quest"));

            HideoutInstance hideoutInstance = Comfort.Common.Singleton<HideoutInstance>.Instance;
            foreach (EFT.Hideout.AreaData ad in hideoutInstance.AreaDatas)
            {
                EFT.Hideout.Stage actualNextStage = ad.NextStage;

                // If we don't want to get requirement of locked to construct areas, skip if it is locked to construct
                if (!HideoutRequirementIndicatorMod.showLockedModules && ad.Status == EFT.Hideout.EAreaStatus.LockedToConstruct)
                {
                    continue;
                }

                // If the area has no future upgrade, skip
                if (ad.Status == EFT.Hideout.EAreaStatus.NoFutureUpgrades)
                {
                    continue;
                }

                // If in process of constructing or upgrading, go to actual next stage if it exists
                if(ad.Status == EFT.Hideout.EAreaStatus.Constructing ||
                   ad.Status == EFT.Hideout.EAreaStatus.Upgrading)
                {
                    actualNextStage = ad.StageAt(ad.NextStage.Level + 1);

                    // If there are not StageAt given level, it will return a new stage, so level will be 0
                    if (actualNextStage.Level == 0)
                    {
                        continue;
                    }
                }

                EFT.Hideout.RelatedRequirements requirements = actualNextStage.Requirements;

                foreach (Requirement requirement in requirements)
                {
                    EFT.Hideout.ItemRequirement itemRequirement = requirement as EFT.Hideout.ItemRequirement;
                    if (itemRequirement != null)
                    {
                        string requirementTemplate = itemRequirement.TemplateId;
                        if (template == requirementTemplate)
                        {
                            // A requirement but already have the amount we need
                            if (requirement.Fulfilled)
                            {
                                // Even if we have enough of this item to fulfill a requirement in one area
                                // we might still need it, and if thats the case we want to show that color, not fulfilled color, so you know you still need more of it
                                // So only set color to fulfilled if not needed
                                if (!foundNeeded && !foundFullfilled)
                                {
                                    // If we want to prioritize quest checkmark, only change the sprite if not a quest item
                                    if(!questItem || !HideoutRequirementIndicatorMod.prioritizeQuest) 
                                    { 
                                        // Following calls base class method ShowGameObject()
                                        // To call base methods without reverse patch, must modify IL code for this line from callvirt to call
                                        (__instance as EFT.UI.UIElement).ShowGameObject(false);
                                        ____questIconImage.sprite = ____foundInRaidSprite;
                                        ____questIconImage.color = new Color(0.23137f, 0.93725f, 1);
                                    }

                                    foundFullfilled = true;
                                }

                                areaNames.Add("<color=#3bdfff>" + ad.Template.Name + "</color>");
                            }
                            else
                            {
                                if (!foundNeeded)
                                {
                                    if(!questItem || !HideoutRequirementIndicatorMod.prioritizeQuest) 
                                    { 
                                        (__instance as EFT.UI.UIElement).ShowGameObject(false);
                                        ____questIconImage.sprite = ____foundInRaidSprite;
                                        ____questIconImage.color = new Color(0.23922f, 1, 0.44314f);
                                    }

                                    foundNeeded = true;
                                }

                                areaNames.Add("<color=#3dff71>" + ad.Template.Name + "</color>");
                            }
                        }
                    }
                }
            }

            if (foundNeeded || foundFullfilled)
            {
                // Build string of list of areas this is needed for
                string areaNamesString = "";
                for (int i = 0; i < areaNames.Count; ++i)
                {
                    areaNamesString += (i == 0 ? "" : (areaNames.Count == 2 ? "" : ",") + (i == areaNames.Count - 1 ? " and " : " ")) + areaNames[i];
                }

                if (___string_3 != null && (item.MarkedAsSpawnedInSession || questItem))
                {
                    ___string_3 += string.Format(" and needed for {0}".Localized(), areaNamesString);
                }
                else
                {
                    ___string_3 = string.Format("Needed for {0}".Localized(), areaNamesString);
                }

                // If this is not a quest item or found in raid, the original returns and the tooltip never gets set, so we need to set it ourselves
                ___simpleTooltip_0 = tooltip;
            }
            else
            {
                // Just to make sure the change is not permanent, because the color is never set back to the default white by EFT
                // Because if an item was a requirement, its sprite's color set to green/blue, then it stopped being a requirement, but it was found in raid/is quest item
                // the sprite would still show up green/blue
                ____questIconImage.color = Color.white;
            }
        }
    }
}