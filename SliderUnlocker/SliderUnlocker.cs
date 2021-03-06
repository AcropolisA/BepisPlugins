﻿using BepInEx;
using ChaCustom;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SliderUnlocker
{
    [BepInPlugin(GUID: "com.bepis.bepinex.sliderunlocker", Name: "Slider Unlocker", Version: "1.5")]
    public class SliderUnlocker : BaseUnityPlugin
    {
        public float Minimum => int.Parse(this.GetEntry("wideslider-minimum", "-100")) / (float)100;
        public float Maximum => int.Parse(this.GetEntry("wideslider-maximum", "200")) / (float)100;

        void Awake()
        {
            Hooks.InstallHooks();
        }

        void LevelFinishedLoading(Scene scene, LoadSceneMode mode)
        {
            SetAllSliders(scene, Minimum, Maximum);
        }


        public void SetAllSliders(Scene scene, float minimum, float maximum)
        {
            List<object> cvsInstances = new List<object>();

            Assembly illusion = typeof(CvsAccessory).Assembly;

            var sceneObjects = scene.GetRootGameObjects();

            foreach (Type type in illusion.GetTypes())
            {
                if (type.Name.ToUpper().StartsWith("CVS") &&
                    type != typeof(CvsDrawCtrl) &&
                    type != typeof(CvsColor))
                {
                    foreach (var obj in sceneObjects)
                    {
                        cvsInstances.AddRange(obj.GetComponentsInChildren(type));
                    }
                }

            }
                
            foreach (object cvs in cvsInstances)
            {
                if (cvs == null)
                    continue;

                var fields = cvs.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                                    .Where(x => typeof(Slider).IsAssignableFrom(x.FieldType));

                foreach (Slider slider in fields.Select(x => x.GetValue(cvs)))
                {
                    if (slider == null)
                        continue;

                    slider.minValue = minimum;
                    slider.maxValue = maximum;
                }
            }
        }

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
    }
}