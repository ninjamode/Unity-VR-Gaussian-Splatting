using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class Cycle : MonoBehaviour
{
    public InputActionProperty nextButton;

    // List of scenes
    public List<GameObject> scenes;
    public int initialScene = 0;

    private int activeScene;

    private void OnEnable()
    {
        nextButton.action.Enable();

        scenes.ForEach(o => o.SetActive(false));
        scenes[initialScene].SetActive(true);
    }
    // Update is called once per frame
    void Update()
    {
        if (nextButton.action.WasPressedThisFrame())
        {
            var nextScene = ++activeScene % scenes.Count;
            Debug.Log($"Cycling to scene {nextScene}");

            scenes.ForEach(o => o.SetActive(false));
            scenes[nextScene].SetActive(true);
        }
    }
}
