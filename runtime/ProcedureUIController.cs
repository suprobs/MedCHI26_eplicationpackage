using UnityEngine;
using TMPro;

namespace EFSM
{
    /// <summary>
    /// Construye su propio canvas world-space por codigo — no requiere crear
    /// elementos de UI manualmente en el editor, tal y como documentaba el
    /// pipeline de referencia.
    /// </summary>
    public class ProcedureUIController : MonoBehaviour
    {
        public EfsmStateManager stateManager;
        private TMP_Text _text;

        private void Awake() => BuildCanvas();

        private void BuildCanvas()
        {
            var canvas = gameObject.GetComponent<Canvas>();
            if (canvas == null) canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            transform.localScale = Vector3.one * 0.001f;

            var rt = gameObject.GetComponent<RectTransform>();
            if (rt == null) rt = gameObject.AddComponent<RectTransform>();
            rt.sizeDelta = new Vector2(400, 150);

            var textGo = new GameObject("InstructionText");
            textGo.transform.SetParent(transform, false);
            _text = textGo.AddComponent<TextMeshProUGUI>();
            _text.fontSize = 28;
            _text.alignment = TextAlignmentOptions.Center;
            _text.text = "Inicializando...";

            var textRect = _text.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
        }

        private void OnEnable()
        {
            if (stateManager == null) stateManager = FindObjectOfType<EfsmStateManager>();
            if (stateManager == null) return;

            stateManager.OnStepAdvanced += HandleStepAdvanced;
            stateManager.OnProcedureComplete += HandleComplete;
            stateManager.OnGuardFailed += HandleGuardFailed;
        }

        private void OnDisable()
        {
            if (stateManager == null) return;
            stateManager.OnStepAdvanced -= HandleStepAdvanced;
            stateManager.OnProcedureComplete -= HandleComplete;
            stateManager.OnGuardFailed -= HandleGuardFailed;
        }

        private void HandleStepAdvanced(StepDef step) =>
            _text.text = !string.IsNullOrEmpty(step.description) ? step.description : $"Paso {step.order}";

        private void HandleComplete() => _text.text = "Procedimiento completado.";

        private void HandleGuardFailed(string guard) => _text.text = "Espera, todavia no puedes hacer eso.";
    }
}
