using UnityEngine;

namespace EFSM
{
    /// <summary>
    /// Extension propia para componentes de tipo "press" (el boton Scan, o el gesto
    /// de "colocar el original" sobre el cristal). El pipeline de referencia solo
    /// contemplaba interacciones de tipo grab (deslizar/rotar via ConstrainedMover +
    /// GrabInteractable) y excluia explicitamente el Building Block "Poke Interaction".
    /// Para no reabrir esa exclusion (documentada como fuente de errores de configuracion
    /// si no se completa correctamente), esta clase implementa una deteccion de
    /// proximidad propia con un BoxCollider en modo trigger, sin depender de Poke
    /// Interaction ni de GrabInteractable.
    ///
    /// Feedback "blink" (nuevo): cuando movement.feedback = "blink" en el device_spec,
    /// el builder activa 'blinkOnStateChange'. El trigger se suscribe entonces a
    /// EfsmStateManager.OnMachineStateChanged y, cuando SU maquina cambia de estado
    /// (la haya movido el propio pulsador, el ITS o una transicion compartida),
    /// dispara un pulso de EfsmHighlightPulse. El parpadeo es asi un EFECTO
    /// OBSERVABLE del cambio de estado que ya orquesta el EfsmStateManager, no una
    /// maquina nueva: se respeta la separacion causa/efecto del metodo y no se
    /// regenera el manager.
    ///
    /// Pendiente de verificacion real en el proyecto Unity del usuario: el layer
    /// exacto que exponen los colliders de la punta de los dedos en el Building
    /// Block "Grab Interaction" de Quest 3 no se ha podido confirmar en este
    /// entorno (sin acceso a Unity ni a la documentacion del SDK instalada). Se
    /// deja `interactorLayers` configurable en el Inspector para ese ajuste.
    /// </summary>
    [RequireComponent(typeof(BoxCollider))]
    [RequireComponent(typeof(EfsmComponentBinder))]
    public class EfsmPressTrigger : MonoBehaviour
    {
        public string action = "press";
        public float cooldownSeconds = 0.6f;
        [Tooltip("Si se deja en 0 (Everything), reacciona a cualquier collider. " +
                 "Restringir al layer de manos/controlador una vez verificado en el proyecto.")]
        public LayerMask interactorLayers;

        [Header("Feedback visual (movement.feedback = \"blink\")")]
        [Tooltip("Si esta activo, cada cambio de estado de la maquina de este componente " +
                 "dispara un pulso de EfsmHighlightPulse. Lo activa el builder desde el spec; " +
                 "tambien se puede marcar a mano.")]
        public bool blinkOnStateChange = false;
        [Tooltip("Color del pulso de confirmacion.")]
        public Color blinkColor = new Color(0.20f, 0.80f, 1f);
        [Tooltip("Duracion total del pulso en segundos.")]
        public float blinkDuration = 0.6f;
        [Tooltip("Numero de parpadeos por activacion.")]
        public int blinkPulses = 1;

        private EfsmComponentBinder _binder;
        private EfsmHighlightPulse _pulse;
        private float _lastTrigger = -999f;
        private bool _subscribed;

        private void Awake()
        {
            _binder = GetComponent<EfsmComponentBinder>();
            _pulse = GetComponent<EfsmHighlightPulse>();
            var col = GetComponent<BoxCollider>();
            col.isTrigger = true;
        }

        private void OnEnable()  { TrySubscribe(); }
        private void OnDisable() { Unsubscribe(); }

        // El EfsmStateManager puede no existir todavia en Awake/OnEnable (orden de
        // inicializacion de escena). Se reintenta la suscripcion perezosamente.
        private void TrySubscribe()
        {
            if (_subscribed || !blinkOnStateChange) return;
            var mgr = EfsmStateManager.Instance;
            if (mgr == null) return;
            mgr.OnMachineStateChanged += OnMachineStateChanged;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            var mgr = EfsmStateManager.Instance;
            if (mgr != null) mgr.OnMachineStateChanged -= OnMachineStateChanged;
            _subscribed = false;
        }

        private void OnMachineStateChanged(string machineId, string from, string to)
        {
            if (_binder == null || machineId != _binder.machineId) return;
            if (_pulse != null) _pulse.Pulse(blinkColor, blinkDuration, Mathf.Max(1, blinkPulses));
        }

        private void OnTriggerEnter(Collider other)
        {
            // Suscripcion perezosa: cubre el caso de que el manager apareciera
            // despues de OnEnable.
            if (blinkOnStateChange && !_subscribed) TrySubscribe();

            if (interactorLayers.value != 0 &&
                (interactorLayers.value & (1 << other.gameObject.layer)) == 0)
                return;

            if (Time.time - _lastTrigger < cooldownSeconds) return;
            _lastTrigger = Time.time;

            Debug.Log($"[EfsmPressTrigger] '{_binder.componentId}' activado (accion: {action}).");
            EfsmStateManager.Instance?.TriggerAction(_binder.machineId, action);
        }
    }
}
