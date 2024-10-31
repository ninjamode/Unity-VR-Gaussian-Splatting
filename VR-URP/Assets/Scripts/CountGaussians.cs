using GaussianSplatting.Runtime;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class CountGaussians : MonoBehaviour
{

    TextMeshProUGUI text;

    private void Start()
    {
        text = GetComponent<TextMeshProUGUI>();
        StartCoroutine(UpdateText());
    }


    IEnumerator UpdateText()
    {
        yield return new WaitForSeconds(1);
        var count = GaussianSplatRenderer.CompleteGaussianCount();
        text.text = $"Current gaussian count: {count}";
        StartCoroutine(UpdateText());
    }
}
