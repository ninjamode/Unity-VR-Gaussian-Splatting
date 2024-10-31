using GaussianSplatting.Runtime;
using JetBrains.Annotations;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

public class AnatomySpawner : MonoBehaviour
{
    public Transform spawnParent;
    public Vector3 spawnOffset = new Vector3(0, 0, 0);
    public List<GSPrefabs> spawnablePrefabs = new();

    public Quality quality;
    public Anatomy anatomy;

    public TextMeshProUGUI qualityText;
    public TextMeshProUGUI anatomyText;

    public void SetQuality(Quality q) {
        quality = q;
        qualityText.text = $"Selected Quality: <b>{q}</b>";
    }
    public void SetAnatomy(Anatomy a) {
        anatomy = a;
        anatomyText.text = $"Selected Anatomy: <b>{a}</b>";
    }

    public void SetHighQuality() => SetQuality(Quality.High);
    public void SetMediumQuality() => SetQuality(Quality.Med);
    public void SetLowQuality() => SetQuality(Quality.Low);

    public void SetAnatomyLeg() => SetAnatomy(Anatomy.Leg);
    public void SetAnatomyFullbody() => SetAnatomy(Anatomy.Fullody);
    public void SetAnatomyTransparency() => SetAnatomy(Anatomy.Transparency);
    public void SetAnatomySynthetic() => SetAnatomy(Anatomy.Synth);


    public void Spawn()
    {
        var obj = spawnablePrefabs.
            Where(sp => sp.type == anatomy).
            Select(sp => sp.anatomyQualityLevels.
                Find(q => q.quality == quality).prefab).
                FirstOrDefault();
        var spawned = Instantiate(obj, spawnParent);
        spawned.transform.Translate(spawnOffset, Space.Self);
    }


    [Serializable]
    public enum Quality
    {
        Low = 0,
        Med = 1,
        High = 2,
    }

    [Serializable]
    public enum Anatomy
    {
        None = 0,
        Leg = 1,
        Fullody = 2,
        Synth = 3,
        Transparency = 4,
    }

    [Serializable]
    public struct GSPrefabs
    {
        [Serializable]
        public struct QualityMapping
        {
            public Quality quality;
            public GaussianSplatRenderer prefab;
        }

        public Anatomy type;
        public List<QualityMapping> anatomyQualityLevels;
    }
}


