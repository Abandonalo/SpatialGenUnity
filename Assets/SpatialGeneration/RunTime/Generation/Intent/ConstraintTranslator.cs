using System;
using System.Collections.Generic;

namespace SpatialGeneration.Generation.Intent
{
    public static class ConstraintTranslator
    {
        public static ConstraintSet Translate(SceneIntent intent)
        {
            ConstraintSet constraintSet = new();
            if (intent?.Proxies == null || intent.Proxies.Count == 0)
                return constraintSet;

            List<ProxyIntent> ordered = new(intent.Proxies);
            ordered.Sort(CompareProxyIntent);

            foreach (ProxyIntent proxy in ordered)
            {
                if (proxy == null)
                    continue;

                if (proxy.Role == ProxyRole.Occupy && proxy.Shape == ProxyShape.Box)
                {
                    constraintSet.Constraints.Add(new Constraint
                    {
                        Type = ConstraintType.OccupyVolume,
                        ProxyId = proxy.Id,
                        TargetLabel = proxy.Label,
                        Weight = proxy.Strength,
                        Priority = proxy.Priority
                    });
                    continue;
                }

                if (proxy.Role == ProxyRole.Avoid && proxy.Shape == ProxyShape.Sphere)
                {
                    constraintSet.Constraints.Add(new Constraint
                    {
                        Type = ConstraintType.KeepEmpty,
                        ProxyId = proxy.Id,
                        Weight = proxy.Strength,
                        Priority = proxy.Priority
                    });
                    continue;
                }

                if (proxy.Role == ProxyRole.Attract && proxy.Shape == ProxyShape.Cylinder)
                {
                    constraintSet.Constraints.Add(new Constraint
                    {
                        Type = ConstraintType.FocusRegion,
                        ProxyId = proxy.Id,
                        Mode = "attention",
                        AxisHint = GetWorldUpFromPose(proxy.Pose),
                        Weight = proxy.Strength,
                        Priority = proxy.Priority
                    });
                }
            }

            return constraintSet;
        }

        private static int CompareProxyIntent(ProxyIntent a, ProxyIntent b)
        {
            int priorityCompare = b.Priority.CompareTo(a.Priority);
            if (priorityCompare != 0)
                return priorityCompare;

            return string.Compare(a.Id, b.Id, StringComparison.Ordinal);
        }

        private static Vector3Data GetWorldUpFromPose(PoseData pose)
        {
            if (pose?.Rotation == null)
                return new Vector3Data { X = 0f, Y = 1f, Z = 0f };

            QuaternionData q = pose.Rotation;
            Vector3Data localUp = new() { X = 0f, Y = 1f, Z = 0f };
            return RotateVectorByQuaternion(localUp, q);
        }

        private static Vector3Data RotateVectorByQuaternion(Vector3Data v, QuaternionData q)
        {
            float qx = q.X;
            float qy = q.Y;
            float qz = q.Z;
            float qw = q.W;

            float qLen = MathF.Sqrt((qx * qx) + (qy * qy) + (qz * qz) + (qw * qw));
            if (qLen <= 1e-6f)
                return new Vector3Data { X = v.X, Y = v.Y, Z = v.Z };

            qx /= qLen;
            qy /= qLen;
            qz /= qLen;
            qw /= qLen;

            float ux = qx;
            float uy = qy;
            float uz = qz;
            float s = qw;

            float dotUV = (ux * v.X) + (uy * v.Y) + (uz * v.Z);
            float dotUU = (ux * ux) + (uy * uy) + (uz * uz);

            float crossX = (uy * v.Z) - (uz * v.Y);
            float crossY = (uz * v.X) - (ux * v.Z);
            float crossZ = (ux * v.Y) - (uy * v.X);

            return new Vector3Data
            {
                X = (2f * dotUV * ux) + ((s * s - dotUU) * v.X) + (2f * s * crossX),
                Y = (2f * dotUV * uy) + ((s * s - dotUU) * v.Y) + (2f * s * crossY),
                Z = (2f * dotUV * uz) + ((s * s - dotUU) * v.Z) + (2f * s * crossZ)
            };
        }
    }
}
