using Assets.Scripts.Entities;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ObstacleMap
{
    public int Id;
    public List<Obstacle> Obstacles = new List<Obstacle>();
    public ObstacleMap(int id)
    {
        Id = id;
    }

    public override bool Equals(System.Object obj)
    {
        if (obj == null)
        {
            return false;
        }

        // If parameter cannot be cast to class return false.
        ObstacleMap x = obj as ObstacleMap;
        if (x == null)
        {
            return false;
        }

        return Id == x.Id;
    }

    public bool Equals(ObstacleMap other)
    {
        return Id == other.Id;
    }

    public override int GetHashCode()
    {
        return Id.GetHashCode();
    }

    public static bool operator ==(ObstacleMap a, ObstacleMap b)
    {
        // If both are null, or both are same instance, return true.
        if (System.Object.ReferenceEquals(a, b))
        {
            return true;
        }

        // If one is null, but not both, return false.
        if (((object)a == null) || ((object)b == null))
        {
            return false;
        }

        return a.Id == b.Id;
    }

    public static bool operator !=(ObstacleMap a, ObstacleMap b)
    {
        return !(a == b);
    }

}
