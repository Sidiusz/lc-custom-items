// Заглушка ScanNodeProperties из игры (Assembly-CSharp), см. Item.cs
using UnityEngine;

public class ScanNodeProperties : MonoBehaviour
{
    public int maxRange;
    public int minRange;
    public bool requiresLineOfSight;
    [Space] public string headerText;
    public string subText;
    public int scrapValue;
    [Space] public int creatureScanID;
    [Space] public int nodeType;
}
