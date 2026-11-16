using UnityEngine;

namespace DontWakeIt.Items
{
    public enum ToolKind
    {
        None,
        Brush,
        Rake,
        Rope,
        Bucket,
        Lantern,
        Radio,
        Hammer,
        Other
    }

    [CreateAssetMenu(menuName ="Dont Wake It/Item", fileName = "NewItem")]
    public class ItemDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Unique and never changes, used for save games and shop")]
        public string id = "item";
        public string displayName = "Item";
        [TextArea] public string description;

        [Header("Job Use")]
        public ToolKind kind = ToolKind.Other;
        [Tooltip("1 = basic, higher = improved")]
        [Min(1)] public int tier = 1;
        [Tooltip("Cost to buy or replace")]
        [Min(0)] public int price = 0;

        [Header("Prefab")]
        [Tooltip("Like come on it's pretty obvious what this is for")]
        public GameObject prefab;

        [Header("Holding")]
        public Vector3 heldLocalPosition = new Vector3(0f, 0f, 0.25f);
        public Vector3 heldLocalEuler = Vector3.zero;

        [Header("Noise")]
        [Tooltip("Multiplier, 0 = silent, 1 = normal, 2 = loud")]
        [Min(0f)] public float impactNoise = 1f;
        [Tooltip("Min speed to make noise")]
        [Min(0f)] public float noiseMinSpeed = 1.5f;
        [Tooltip("Impact speed that counts as a noise")]
        [Min(0.1f)] public float noiseFullSpeed = 8f;
    }
}