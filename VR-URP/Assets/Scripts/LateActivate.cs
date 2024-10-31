using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class LateActivate : MonoBehaviour
{
    public List<GameObject> gameObjects;
    public float afterSec = 1;

    void Start()
    {
        StartCoroutine(ActivateAfter());
    }

    IEnumerator ActivateAfter()
    {
        yield return new WaitForSeconds(afterSec);
        gameObjects.ForEach(gameObject => gameObject.SetActive(true));
    }
}
