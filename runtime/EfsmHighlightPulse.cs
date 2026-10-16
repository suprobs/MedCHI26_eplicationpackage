using System.Collections;
using UnityEngine;

namespace EFSM
{
    /// <summary>
    /// Utilidad de resalte visual reutilizable. No depende de URP/HDRP-specific
    /// outline shaders (no he podido verificar en este entorno qué render pipeline
    /// usa el proyecto del usuario) — usa el camino mas compatible: pulso de color
    /// del material estandar, que funciona igual en Built-in, URP o HDRP siempre
    /// que el material exponga la propiedad "_Color" o "_BaseColor".
    /// </summary>
    public class EfsmHighlightPulse : MonoBehaviour
    {
        private Renderer _renderer;
        private Color _baseColor;
        private Coroutine _running;

        private void Awake()
        {
            _renderer = GetComponentInChildren<Renderer>();
            if (_renderer != null) _baseColor = _renderer.material.color;
        }

        public void Pulse(Color color, float duration = 1.2f, int pulses = 2)
        {
            if (_renderer == null) return;
            if (_running != null) StopCoroutine(_running);
            _running = StartCoroutine(PulseRoutine(color, duration, pulses));
        }

        private IEnumerator PulseRoutine(Color color, float duration, int pulses)
        {
            var perPulse = duration / Mathf.Max(1, pulses);
            for (int i = 0; i < pulses; i++)
            {
                float t = 0f;
                while (t < perPulse / 2f)
                {
                    t += Time.deltaTime;
                    _renderer.material.color = Color.Lerp(_baseColor, color, t / (perPulse / 2f));
                    yield return null;
                }
                t = 0f;
                while (t < perPulse / 2f)
                {
                    t += Time.deltaTime;
                    _renderer.material.color = Color.Lerp(color, _baseColor, t / (perPulse / 2f));
                    yield return null;
                }
            }
            _renderer.material.color = _baseColor;
            _running = null;
        }
    }
}
