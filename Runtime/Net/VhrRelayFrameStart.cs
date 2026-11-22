using UnityEngine;

namespace VhrGames.Sdk
{
    /// <summary>
    /// Отметка начала кадра для пачек релея (<see cref="VhrRelayPump.InFrame"/>): самый
    /// ранний Update кадра. Живёт на том же скрытом объекте, что и насос.
    /// </summary>
    [AddComponentMenu("")]
    [DefaultExecutionOrder(-32000)]
    internal sealed class VhrRelayFrameStart : MonoBehaviour
    {
        private void Update() => VhrRelayPump.InFrame = true;
    }
}
