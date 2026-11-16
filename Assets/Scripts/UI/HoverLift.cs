using UnityEngine;
using UnityEngine.EventSystems;

namespace DontWakeIt.UI
{
    public class HoverLift : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        public float hoverScale = 1.04f;
        public float pressScale = 0.97f;

        private bool hover, pressed;
        private float scale = 1f;

        public void OnPointerEnter(PointerEventData e) => hover = true;
        public void OnPointerExit(PointerEventData e) { hover = false; pressed = false; }
        public void OnPointerDown(PointerEventData e) => pressed = true;
        public void OnPointerUp(PointerEventData e) => pressed = false;

        private void OnDisable()
        {
            hover = pressed = false;
            scale = 1f;
            transform.localScale = Vector3.one;
        }

        void Update()
        {
            float target = pressed ? pressScale : hover ? hoverScale : 1f;
            scale = Mathf.Lerp(scale, target, 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime));
            transform.localScale = new Vector3(scale, scale, 1f);
        }
    }
}
