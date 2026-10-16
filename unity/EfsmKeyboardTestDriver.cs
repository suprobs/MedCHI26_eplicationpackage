using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace EFSM
{
    /// <summary>
    /// Driver de teclado para probar la simulacion SIN visor (HMD no disponible).
    /// Simula las acciones del usuario en la capa SEMANTICA, por los mismos
    /// caminos de codigo que la interaccion real:
    ///
    ///   - Componentes con ConstrainedMover (tapa): reproduce el ciclo completo
    ///     agarrar -> mover -> soltar (OnGrabStart, MoveTo animado, OnGrabEnd) y
    ///     al soltar aplica la MISMA logica de umbrales que
    ///     OVRGrabbableAdapter.HandleGrabEnd (mantener en paridad si aquella cambia).
    ///   - Componentes con EfsmPressTrigger (cristal, boton): dispara
    ///     EfsmStateManager.TriggerAction con el mismo par (machineId, action)
    ///     que OnTriggerEnter, de modo que guardas, fallos e ITS reaccionan
    ///     exactamente igual que en el dispositivo.
    ///
    /// Lo que NO cubre: la interaccion fisica real (pinch + OneGrabRotateTransformer).
    /// Esa verificacion requiere visor y queda pendiente hasta recuperarlo.
    ///
    /// Uso: anadir a cualquier GameObject de la escena (p. ej. el del builder).
    /// Al entrar en Play descubre los objetos interactivos y asigna teclas
    /// numericas en orden de descubrimiento; el mapa se imprime en consola y se
    /// muestra en un overlay en pantalla.
    ///   [tecla]         -> abrir por completo / disparar trigger
    ///   Shift + [tecla] -> cerrar por completo (movers) / accion INVERSA del
    ///                      componente (triggers: p. ej. remove_cup si la unica
    ///                      accion del volumen es place_cup)
    ///   Alt + [tecla]   -> apertura parcial al 50%, sin accion (para probar
    ///                      umbrales, pistas por inactividad y timeouts del ITS)
    ///   [N] / boton en pantalla / menu contextual -> avanzar un paso a la fuerza
    ///                      (equivale a Force Next Step del EfsmStateManager; util
    ///                      cuando un paso no tiene accion mapeada en el teclado)
    ///
    /// IMPORTANTE: retirar o desactivar este componente en las sesiones con
    /// usuarios reales (Bloque 2 del experimento) — un atajo de teclado activo
    /// invalidaria las metricas de sesion.
    /// </summary>
    public class EfsmKeyboardTestDriver : MonoBehaviour
    {
        [Tooltip("Duracion en segundos de la animacion de apertura/cierre simulada.")]
        public float grabDuration = 0.8f;

        [Tooltip("Mostrar el mapa de teclas como overlay en pantalla.")]
        public bool showOverlay = true;

        [Tooltip("Tecla para forzar el avance de un paso (ademas del boton en pantalla y del menu contextual).")]
        public KeyCode skipStepKey = KeyCode.N;

        private static readonly KeyCode[] Keys =
        {
            KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5,
            KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9
        };

        private enum BindingKind { Mover, Trigger }

        private class Binding
        {
            public KeyCode key;
            public BindingKind kind;
            public string label;
            public ConstrainedMover mover;
            public EfsmPressTrigger trigger;
            public EfsmComponentBinder binder;
            public bool busy; // evita animaciones concurrentes sobre el mismo mover
        }

        private readonly List<Binding> _bindings = new List<Binding>();

        private void Start()
        {
            DiscoverBindings();
            LogBindings();
        }

        private void DiscoverBindings()
        {
            _bindings.Clear();
            int keyIndex = 0;

            // Movers primero (suelen ser el paso 1 de los procedimientos).
            foreach (var mover in FindObjectsOfType<ConstrainedMover>())
            {
                if (keyIndex >= Keys.Length) break;
                var binder = mover.GetComponent<EfsmComponentBinder>();
                if (binder == null) continue;
                _bindings.Add(new Binding
                {
                    key = Keys[keyIndex++],
                    kind = BindingKind.Mover,
                    label = $"{binder.componentId} (abrir / Shift=cerrar / Alt=parcial)",
                    mover = mover,
                    binder = binder
                });
            }

            foreach (var trigger in FindObjectsOfType<EfsmPressTrigger>())
            {
                if (keyIndex >= Keys.Length) break;
                var binder = trigger.GetComponent<EfsmComponentBinder>();
                if (binder == null) continue;
                _bindings.Add(new Binding
                {
                    key = Keys[keyIndex++],
                    kind = BindingKind.Trigger,
                    label = $"{binder.componentId} -> '{trigger.action}' (Shift=inversa)",
                    trigger = trigger,
                    binder = binder
                });
            }
        }

        private void LogBindings()
        {
            if (_bindings.Count == 0)
            {
                Debug.LogWarning("[EfsmKeyboardTestDriver] No se han encontrado ConstrainedMover ni EfsmPressTrigger en la escena. " +
                                 "Comprobar que la geometria esta construida (Build en Editor) antes del Play.");
                return;
            }

            var lines = "[EfsmKeyboardTestDriver] Mapa de teclas:\n";
            foreach (var b in _bindings) lines += $"  [{b.key.ToString().Replace("Alpha", "")}] {b.label}\n";
            Debug.Log(lines);
        }

        private void Update()
        {
            if (Input.GetKeyDown(skipStepKey)) AdvanceStep();

            foreach (var b in _bindings)
            {
                if (!Input.GetKeyDown(b.key)) continue;

                if (b.kind == BindingKind.Trigger)
                {
                    bool inverse = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                    // Con Shift se busca la accion INVERSA del componente: la primera
                    // transicion de su maquina aplicable desde el estado actual cuya
                    // accion no sea la del trigger (p. ej. remove_cup cuando la taza ya
                    // esta colocada). Cubre los volumenes de un solo action (limitacion
                    // detectada en el paso "Remove the cup" de la cafetera).
                    string action = inverse ? FindAlternativeAction(b) : b.trigger.action;
                    if (string.IsNullOrEmpty(action))
                    {
                        Debug.LogWarning($"[EfsmKeyboardTestDriver] '{b.binder.componentId}': sin accion inversa aplicable desde el estado actual.");
                        continue;
                    }
                    // Mismo camino de codigo que EfsmPressTrigger.OnTriggerEnter:
                    // el EFSM evalua guardas y el ITS registra exito o fallo igual
                    // que si el dedo hubiera entrado en el volumen.
                    Debug.Log($"[EfsmKeyboardTestDriver] Simulando trigger '{action}' en '{b.binder.componentId}'.");
                    EfsmStateManager.Instance?.TriggerAction(b.binder.machineId, action);
                }
                else if (!b.busy)
                {
                    bool close = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                    bool partial = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
                    float target = partial ? 0.5f : (close ? 0f : 1f);
                    StartCoroutine(SimulateGrabMove(b, target));
                }
            }
        }

        private IEnumerator SimulateGrabMove(Binding b, float targetProgress)
        {
            b.busy = true;
            float start = b.mover.GetNormalizedProgress();
            Debug.Log($"[EfsmKeyboardTestDriver] Simulando agarre de '{b.binder.componentId}': progreso {start:F2} -> {targetProgress:F2}.");

            b.mover.OnGrabStart();

            float t = 0f;
            while (t < grabDuration)
            {
                t += Time.deltaTime;
                b.mover.MoveTo(Mathf.Lerp(start, targetProgress, Mathf.Clamp01(t / grabDuration)));
                yield return null;
            }
            b.mover.MoveTo(targetProgress);

            b.mover.OnGrabEnd();

            // PARIDAD con OVRGrabbableAdapter.HandleGrabEnd: misma decision de
            // accion por umbrales. Si aquella logica cambia, cambiar esta.
            var progress = b.mover.GetNormalizedProgress();
            string action = null;
            if (progress >= b.mover.openThreshold) action = b.mover.actionAtMax;
            else if (progress <= b.mover.closedThreshold) action = b.mover.actionAtMin;

            Debug.Log($"[EfsmKeyboardTestDriver] '{b.binder.componentId}' soltado con progreso {progress * 100f:F0}%. " +
                      (string.IsNullOrEmpty(action) ? "Sin accion (zona intermedia)." : $"Accion: '{action}'."));

            if (!string.IsNullOrEmpty(action))
                EfsmStateManager.Instance?.TriggerAction(b.binder.machineId, action);

            b.busy = false;
        }

        /// <summary>
        /// Primera transicion de la maquina del componente aplicable desde el estado
        /// actual y distinta de la accion del trigger. Permite disparar por teclado la
        /// accion de vuelta (remove/close/unplug...) de volumenes con un solo action.
        /// </summary>
        private string FindAlternativeAction(Binding b)
        {
            var mgr = EfsmStateManager.Instance;
            var machine = mgr != null && mgr.loader != null ? mgr.loader.MachineById(b.binder.machineId) : null;
            if (machine == null) return null;
            foreach (var t in machine.transitions)
            {
                if (t.action == b.trigger.action) continue;
                var current = mgr.GetDimensionValue(b.binder.machineId, t.dimension);
                if (current != null && current == t.from) return t.action;
            }
            return null;
        }

        /// <summary>
        /// Fuerza el avance de un paso del procedimiento. Disponible como tecla
        /// (skipStepKey), boton del overlay y menu contextual del componente en el
        /// Inspector. Es un atajo de PRUEBA: el log del ITS no registrara la accion
        /// fisica del paso saltado.
        /// </summary>
        [ContextMenu("Avanzar Paso (Force Next Step)")]
        public void AdvanceStep()
        {
            Debug.Log("[EfsmKeyboardTestDriver] Avance de paso forzado por el driver de teclado.");
            EfsmStateManager.Instance?.ForceNextStep();
        }

        private void OnGUI()
        {
            if (!showOverlay || _bindings.Count == 0) return;

            var sb = new System.Text.StringBuilder("MODO TEST TECLADO (sin visor)\n");
            foreach (var b in _bindings) sb.AppendLine($"[{b.key.ToString().Replace("Alpha", "")}] {b.label}");
            sb.AppendLine($"Shift=cerrar/inversa | Alt=parcial 50% | [{skipStepKey}]=avanzar paso");

            float boxH = 30 + 20 * (_bindings.Count + 1);
            GUI.Box(new Rect(10, 10, 380, boxH), sb.ToString());
            if (GUI.Button(new Rect(10, 14 + boxH, 200, 26), "\u25b6 Avanzar paso (skip)"))
                AdvanceStep();
        }
    }
}