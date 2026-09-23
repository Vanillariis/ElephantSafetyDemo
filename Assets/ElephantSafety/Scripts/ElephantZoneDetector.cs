using UnityEngine;

public enum ElephantZone
{
    Outside,
    Comfort,
    Alert,
    Warning,
    Critical
}

public class ElephantZoneDetector : MonoBehaviour
{
    [Header("Player")]
    public Transform player;
    
    [Header("Elephant")]
    public Animator elephantAnimator;

    [Header("Zone Distances")]
    public float criticalDistance = 5f;
    public float warningDistance = 10f;
    public float alertDistance = 15f;
    public float comfortDistance = 20f;

    private ElephantZone currentZone = ElephantZone.Outside;
    private ElephantZone previousZone = ElephantZone.Outside;

    private void Update()
    {
        //Debug.Log("Zone detector is running!");
        
        if (player == null)
            return;

        float distance = Vector3.Distance(
            transform.position,
            player.position
        );

        ElephantZone newZone;

        if (distance <= criticalDistance)
        {
            newZone = ElephantZone.Critical;
        }
        else if (distance <= warningDistance)
        {
            newZone = ElephantZone.Warning;
        }
        else if (distance <= alertDistance)
        {
            newZone = ElephantZone.Alert;
        }
        else if (distance <= comfortDistance)
        {
            newZone = ElephantZone.Comfort;
        }
        else
        {
            newZone = ElephantZone.Outside;
        }

        // Only print when we actually change zones
        if (newZone != currentZone)
        {
            previousZone = currentZone;
            currentZone = newZone;

            Debug.Log(
                "Elephant Zone: " + currentZone +
                " | Distance: " + distance.ToString("F1") + "m"
            );

            // Only react aggressively if the player moved closer
            if (currentZone > previousZone)
            {
                if (currentZone == ElephantZone.Alert)
                {
                    elephantAnimator.SetTrigger("Attack1");
                }
                else if (currentZone == ElephantZone.Warning)
                {
                    elephantAnimator.SetTrigger("Attack2");
                }
                else if (currentZone == ElephantZone.Critical)
                {
                    elephantAnimator.SetTrigger("Attack3");
                }
            }
        }
    }
}