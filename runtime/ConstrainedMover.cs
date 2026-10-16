using UnityEngine;

namespace EFSM
{
    public enum MovementType { SlideHorizontal, SlideVertical, SlideDepth, FlipHinge, RotateAxis, None }

    /// <summary>
    /// SENSOR de progreso sobre un movimiento restringido. NO mueve el objeto
    /// mientras esta agarrado: desde la adopcion de la base Editor-first, el
    /// movimiento restringido lo ejecuta el transformer nativo del SDK
    /// (OneGrabRotateTransformer para bisagras, OneGrabTranslateTransformer para
    /// deslizamientos), configurado en el Inspector. Este script se limita a:
    ///   1. Leer el transform durante el agarre y traducirlo a progreso 0..1.
    ///   2. Hacer snap a los extremos al soltar (cuando el SDK ya no controla).
    ///   3. Exponer umbrales y acciones EFSM (lift/close) que consume el adapter.
    /// La version anterior reescribia el transform cada frame durante el agarre
    /// (proyeccion sobre el eje permitido); con el transformer del SDK eso seria
    /// una pelea de dos escritores sobre localRotation, por eso se elimina.
    /// </summary>
    [RequireComponent(typeof(EfsmComponentBinder))]
    public class ConstrainedMover : MonoBehaviour
    {
        public MovementType movementType = MovementType.None;

        [Header("Slide settings (deben coincidir con OneGrabTranslateTransformer)")]
        public Vector3 localAxis = Vector3.forward;
        public float limitMin = 0f;
        public float limitMax = 0.1f;

        [Header("Rotation settings (deben coincidir con OneGrabRotateTransformer)")]
        [Tooltip("Mismo eje que el Rotation Axis del transformer. Si el progreso corre al reves (abre=0, cierra=1), invertir el signo aqui.")]
        public Vector3 localRotationAxis = Vector3.right;
        public float angleMin = 0f;
        public float angleMax = 70f;

        [Header("Thresholds")]
        public float openThreshold = 0.85f;
        public float closedThreshold = 0.15f;
        public bool snapOnRelease = true;
        public float snapSpeed = 4f;

        [Header("Acciones EFSM disparadas al soltar")]
        [Tooltip("Accion de la maquina a invocar cuando el progreso al soltar queda por encima de openThreshold.")]
        public string actionAtMax = "";
        [Tooltip("Accion de la maquina a invocar cuando el progreso al soltar queda por debajo de closedThreshold.")]
        public string actionAtMin = "";

        private Vector3 _originLocalPos;
        private Quaternion _originLocalRot;
        private float _progress; // 0..1
        private bool _isGrabbed;
        private float _snapTarget;

        private void Awake()
        {
            _originLocalPos = transform.localPosition;
            _originLocalRot = transform.localRotation;
        }

        public void OnGrabStart() => _isGrabbed = true;

        public void OnGrabEnd()
        {
            _isGrabbed = false;
            if (!snapOnRelease) return;
            _snapTarget = _progress >= openThreshold ? 1f : (_progress <= closedThreshold ? 0f : _progress);
        }

        public float GetNormalizedProgress() => _progress;

        /// <summary>
        /// Mueve el objeto a un progreso 0..1. Solo debe usarse cuando el SDK no
        /// tiene el control: snap tras soltar, reset del procedimiento, o
        /// demostraciones dirigidas por el ITS. Nunca durante un agarre activo.
        /// </summary>
        public void MoveTo(float normalized)
        {
            _progress = Mathf.Clamp01(normalized);

            switch (movementType)
            {
                case MovementType.SlideHorizontal:
                case MovementType.SlideVertical:
                case MovementType.SlideDepth:
                    var offset = Mathf.Lerp(limitMin, limitMax, _progress);
                    transform.localPosition = _originLocalPos + localAxis.normalized * offset;
                    break;

                case MovementType.FlipHinge:
                case MovementType.RotateAxis:
                    var angle = Mathf.Lerp(angleMin, angleMax, _progress);
                    transform.localRotation = _originLocalRot * Quaternion.AngleAxis(angle, localRotationAxis.normalized);
                    break;

                case MovementType.None:
                default:
                    break;
            }
        }

        private void Update()
        {
            if (movementType == MovementType.None) return;

            if (_isGrabbed)
            {
                // Solo LECTURA: el transformer del SDK ya restringe el movimiento
                // al eje/angulo permitido; aqui unicamente se traduce a progreso.
                ReadProgressFromCurrentTransform();
                return;
            }

            if (!snapOnRelease) return;
            if (Mathf.Approximately(_progress, _snapTarget)) return;

            MoveTo(Mathf.MoveTowards(_progress, _snapTarget, snapSpeed * Time.deltaTime));
        }

        private void ReadProgressFromCurrentTransform()
        {
            switch (movementType)
            {
                case MovementType.SlideHorizontal:
                case MovementType.SlideVertical:
                case MovementType.SlideDepth:
                {
                    var offsetVector = transform.localPosition - _originLocalPos;
                    var distanceAlongAxis = Vector3.Dot(offsetVector, localAxis.normalized);
                    _progress = Mathf.Clamp01(Mathf.InverseLerp(limitMin, limitMax, distanceAlongAxis));
                    break;
                }

                case MovementType.FlipHinge:
                case MovementType.RotateAxis:
                {
                    var deltaRot = Quaternion.Inverse(_originLocalRot) * transform.localRotation;
                    deltaRot.ToAngleAxis(out float angle, out Vector3 axis);
                    // ToAngleAxis devuelve siempre angulo positivo (0-360) y puede
                    // invertir el eje para compensar: se corrige el signo comparando
                    // con el eje configurado. Si el progreso corre al reves en la
                    // practica, invertir el signo de localRotationAxis en el
                    // Inspector (ajuste de un segundo, ya no es un cambio de codigo).
                    if (Vector3.Dot(axis, localRotationAxis.normalized) < 0f) angle = -angle;
                    if (angle > 180f) angle -= 360f;
                    _progress = Mathf.Clamp01(Mathf.InverseLerp(angleMin, angleMax, angle));
                    break;
                }

                default:
                    break;
            }
        }
    }
}