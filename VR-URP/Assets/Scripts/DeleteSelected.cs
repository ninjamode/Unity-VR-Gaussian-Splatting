using MixedReality.Toolkit;
using MixedReality.Toolkit.Input;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

public class DeleteSelected : MonoBehaviour
{
    public InputActionProperty deleteLeft;
    public InputActionProperty deleteRight;
    public XRRayInteractor leftRay;
    public XRRayInteractor rightRay;

    private readonly List<IXRInteractable> interactableList = new();

    private void OnEnable()
    {
        deleteLeft.action.Enable();
        deleteLeft.action.performed += DeleteObject;
        deleteRight.action.Enable();
        deleteRight.action.performed += DeleteObject;
    }

    private void OnDisable()
    {
        deleteLeft.action.Disable();
        deleteLeft.action.performed -= DeleteObject;
        deleteRight.action.Disable();
        deleteRight.action.performed -= DeleteObject;
    }

    void DeleteObject(InputAction.CallbackContext ctx)
    {
        if (ctx.performed)
        {
            if (ctx.action.name.ToLower().Contains("right"))
            {
                rightRay.GetValidTargets(interactableList);
            }
            else
            {
                leftRay.GetValidTargets(interactableList);
            }
            Destroy(interactableList.Select(i => i.transform.gameObject).FirstOrDefault());
        }
    }
}
