using UnityEngine;

public class ElephantZoneVisualizer : MonoBehaviour
{
    private ElephantZoneDetector detector;

    private void OnDrawGizmos()
    {
        // Get the detector on this same GameObject
        if (detector == null)
        {
            detector = GetComponent<ElephantZoneDetector>();
        }

        if (detector == null)
            return;

        DrawCircle(transform.position, detector.criticalDistance, Color.red);
        DrawCircle(transform.position, detector.warningDistance, new Color(1f, 0.4f, 0f));
        DrawCircle(transform.position, detector.alertDistance, Color.yellow);
        DrawCircle(transform.position, detector.comfortDistance, Color.green);
    }

    private void DrawCircle(Vector3 center, float radius, Color color)
    {
        Gizmos.color = color;

        const int segments = 64;
        Vector3 previousPoint = center + new Vector3(radius, 0f, 0f);

        for (int i = 1; i <= segments; i++)
        {
            float angle = i * Mathf.PI * 2f / segments;

            Vector3 newPoint = center + new Vector3(
                Mathf.Cos(angle) * radius,
                0f,
                Mathf.Sin(angle) * radius
            );

            Gizmos.DrawLine(previousPoint, newPoint);
            previousPoint = newPoint;
        }
    }
}