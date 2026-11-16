using System.Collections.Generic;
using UnityEngine;

namespace DontWakeIt.Items
{
    [CreateAssetMenu(menuName = "Dont Wake It/Item Catalog", fileName = "ItemCatalog")]
    public class ItemCatalog : ScriptableObject
    {
        public List<ItemDefinition> items = new List<ItemDefinition>();

        public bool TryGet(string id, out ItemDefinition definition)
        {
            foreach(var item in items)
            {
                if(item && item.id == id)
                {
                    definition = item;
                    return true;
                }
            }
            definition = null;
            return false;
        }
    }
}