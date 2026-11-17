// SPDX-License-Identifier: MIT
using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Gaussians.Core
{
    
    // To avoid warnings on newer Unity versions; however, 6.3 doesnt support FindByType without sort mode
    // Can remove all of this as soon as 6.3 support is droppen (not any time soon tho)
    internal static class GaussianObjectSearch
    {
        internal static T[] FindActive<T>() where T : Object
        {
#if UNITY_6000_4_OR_NEWER
            return Object.FindObjectsByType<T>();
#else
            return Object.FindObjectsByType<T>(FindObjectsSortMode.None);
#endif
        }

#if UNITY_EDITOR
        internal static Object[] FindActive(Type type)
        {
#if UNITY_6000_4_OR_NEWER
            return Object.FindObjectsByType(type);
#else
            return Object.FindObjectsByType(type, FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#endif
        }

        internal static T[] FindActiveInInstanceIdOrder<T>() where T : Object
        {
#if UNITY_6000_4_OR_NEWER
            var objects = FindActive<T>();
            // The first matching cutout wins. Keep sorting by the legacy signed instance ID.
            Array.Sort(objects, (left, right) => LegacyInstanceId(left).CompareTo(LegacyInstanceId(right)));
            return objects;
#else
            return Object.FindObjectsByType<T>(FindObjectsSortMode.InstanceID);
#endif
        }

#if UNITY_6000_4_OR_NEWER
        static int LegacyInstanceId(Object obj) => unchecked((int)EntityId.ToULong(obj.GetEntityId()));
#endif
#endif
    }
}
