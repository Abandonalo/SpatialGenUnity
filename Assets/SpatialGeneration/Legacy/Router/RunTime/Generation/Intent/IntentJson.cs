using System;
using UnityEngine;

namespace SpatialGeneration.Generation.Intent
{
    public static class IntentJson
    {
        public static string SerializeSceneIntent(SceneIntent sceneIntent)
        {
            return JsonUtility.ToJson(sceneIntent, true);
        }

        public static SceneIntent DeserializeSceneIntent(string json)
        {
            SceneIntent sceneIntent = JsonUtility.FromJson<SceneIntent>(json);
            if (sceneIntent == null)
                throw new InvalidOperationException("SceneIntent deserialization returned null.");
            return sceneIntent;
        }

        public static string SerializeConstraintSet(ConstraintSet constraintSet)
        {
            return JsonUtility.ToJson(constraintSet, true);
        }

        public static ConstraintSet DeserializeConstraintSet(string json)
        {
            ConstraintSet constraintSet = JsonUtility.FromJson<ConstraintSet>(json);
            if (constraintSet == null)
                throw new InvalidOperationException("ConstraintSet deserialization returned null.");
            return constraintSet;
        }

        public static bool HasStableSceneIntentRoundTrip(SceneIntent sceneIntent)
        {
            string json = SerializeSceneIntent(sceneIntent);
            SceneIntent roundTrip = DeserializeSceneIntent(json);
            string json2 = SerializeSceneIntent(roundTrip);
            return string.Equals(json, json2, StringComparison.Ordinal);
        }

        public static bool HasStableConstraintSetRoundTrip(ConstraintSet constraintSet)
        {
            string json = SerializeConstraintSet(constraintSet);
            ConstraintSet roundTrip = DeserializeConstraintSet(json);
            string json2 = SerializeConstraintSet(roundTrip);
            return string.Equals(json, json2, StringComparison.Ordinal);
        }
    }
}
