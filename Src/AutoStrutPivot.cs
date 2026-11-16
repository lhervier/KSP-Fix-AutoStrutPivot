using System.Collections.Generic;
using Expansions.Serenity;
using HarmonyLib;
using UnityEngine;

namespace com.github.lhervier.ksp.fix.autostrutpivot
{
    /// <summary>
    /// Keeps the autostrut of an unlocked robotic part in Heaviest mode on one side of the part's own pivot.
    ///
    /// To find the heaviest part, KSP walks the vessel from the robotic part and stops at every unlocked
    /// robotic part it meets, but never at the one it starts from: it can pick a part beyond that part's own
    /// moving side. The joint, though, leaves from the side its node gives: the base when the robotic part is
    /// the root, or whenever the heaviest part is found among its children. The strut then joins both sides of
    /// the pivot, and the robotic part can no longer move what it carries (a piston, root of its vessel, strutted
    /// to a module docked at the end of its rod, measured on the Mun).
    /// </summary>
    internal static class AutoStrutPivot
    {
        // Part.GetMassivePart starts from the mass it found earlier in the same physics tick, if any: cleared
        // before searching again, or the second search finds nothing lighter than the first one's result.
        private static readonly AccessTools.FieldRef<Part, Vessel> AutoStrutCacheVessel =
            AccessTools.FieldRefAccess<Part, Vessel>("autoStrutCacheVessel");

        /// <summary>Patches Part.GetAutoStrutAnchor with <see cref="Postfix"/>.</summary>
        public static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(Part), "GetAutoStrutAnchor");
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(AutoStrutPivot), nameof(Postfix)));
        }

        /// <summary>
        /// When the anchor <paramref name="__result"/> KSP found for the autostrut of <paramref name="__instance"/>,
        /// an unlocked robotic part in Heaviest mode, lies on the other side of its pivot from the rigidbody
        /// the joint would leave from, replaces it (and the nodes Part.SecureAutoStrut builds the joint from)
        /// with the heaviest part on the joint's side, or with no anchor at all if there is none. Leaves every
        /// other autostrut as KSP found it. In flight only.
        /// </summary>
        private static void Postfix(Part __instance, ref Part __result, ref AttachNode nodeToParent,
            ref AttachNode nodeFromParent, ref bool srfAttached)
        {
            if (!HighLogic.LoadedSceneIsFlight || __result == null || __result == __instance)
            {
                return;
            }
            if (__instance.autoStrutMode != Part.AutoStrutMode.Heaviest
                && __instance.autoStrutMode != Part.AutoStrutMode.ForceHeaviest)
            {
                return;
            }
            BaseServo servo = __instance.FindModuleImplementing<BaseServo>();
            if (servo == null || !__instance.HasFreePivot())
            {
                return;
            }
            Rigidbody hostBody = HostBody(__instance, servo, nodeToParent, srfAttached);
            if (hostBody == SideBody(__instance, servo, Neighbour(__instance, __result)))
            {
                return;
            }

            // Same search, every part beyond the pivot (from the joint's side) left out of the candidates.
            List<Part> excluded = OtherSide(__instance, servo, hostBody);
            if (__instance.autoStrutExcludeParent && __instance.parent != null)
            {
                excluded.Add(__instance.parent);
            }
            AutoStrutCacheVessel(__instance) = null;
            Part anchor = __instance.GetMassivePart(__instance, out _, out AttachNode newNodeToParent,
                out AttachNode newNodeFromParent, out bool newSrfAttached, excluded.ToArray());

            // The new anchor may change the joint's node, hence its side: checked again.
            if (anchor != null && anchor != __instance
                && HostBody(__instance, servo, newNodeToParent, newSrfAttached)
                    != SideBody(__instance, servo, Neighbour(__instance, anchor)))
            {
                anchor = null;
            }
            Log.Debug($"Autostrut of {Name(__instance)} would cross its own pivot to {Name(__result)}: " +
                (anchor != null && anchor != __instance ? $"anchored to {Name(anchor)} instead" : "no autostrut"));
            __result = anchor;
            nodeToParent = newNodeToParent;
            nodeFromParent = newNodeFromParent;
            srfAttached = newSrfAttached;
        }

        /// <summary>
        /// The rigidbody of robotic part <paramref name="part"/> that PartJoint.Create gives the autostrut
        /// Part.SecureAutoStrut builds from <paramref name="nodeToParent"/> and <paramref name="srfAttached"/>.
        /// </summary>
        private static Rigidbody HostBody(Part part, BaseServo servo, AttachNode nodeToParent, bool srfAttached)
        {
            if (nodeToParent != null && srfAttached)
            {
                // Surface strut: the servo node closest to the strut's position.
                AttachNode closest = null;
                float closestDistance = float.MaxValue;
                foreach (AttachNode node in part.attachNodes)
                {
                    float distance = Vector3.Distance(part.strutOffset, node.position);
                    if (distance < closestDistance)
                    {
                        closest = node;
                        closestDistance = distance;
                    }
                }
                return servo.NodeRigidBody(closest);
            }
            // Stack strut: the node's id, "AutoStrut" (matching no servo node) without a node.
            AttachNode strutNode = new AttachNode
            {
                id = nodeToParent != null ? nodeToParent.id : "AutoStrut",
                nodeType = AttachNode.NodeType.Stack
            };
            return servo.AttachServoRigidBody(strutNode);
        }

        /// <summary>
        /// The rigidbody of robotic part <paramref name="part"/> that holds <paramref name="neighbour"/>, its
        /// parent or one of its children.
        /// </summary>
        private static Rigidbody SideBody(Part part, BaseServo servo, Part neighbour)
        {
            AttachNode node = NodeTo(part, neighbour);
            return node != null ? servo.AttachServoRigidBody(node) : part.Rigidbody;
        }

        /// <summary>
        /// The node joining <paramref name="part"/> to <paramref name="neighbour"/>, as Part.AttachedPartNode
        /// picks it: the node of <paramref name="part"/> attached to it, unless one of the two is surface
        /// attached to the other. Null if none.
        /// </summary>
        private static AttachNode NodeTo(Part part, Part neighbour)
        {
            if (neighbour == null)
            {
                return null;
            }
            AttachNode result = null;
            foreach (AttachNode node in part.attachNodes)
            {
                if (node.attachedPart != null && node.attachedPart.persistentId == neighbour.persistentId)
                {
                    result = node;
                    break;
                }
            }
            if (part.srfAttachNode != null && part.srfAttachNode.attachedPart != null
                && part.srfAttachNode.attachedPart.persistentId == neighbour.persistentId)
            {
                result = part.srfAttachNode;
            }
            else if (neighbour.srfAttachNode != null && neighbour.srfAttachNode.attachedPart != null
                && neighbour.srfAttachNode.attachedPart.persistentId == part.persistentId)
            {
                result = neighbour.srfAttachNode;
            }
            return result;
        }

        /// <summary>
        /// The neighbour of <paramref name="part"/> (its parent or one of its children) through which the tree of
        /// the vessel leads to <paramref name="target"/>.
        /// </summary>
        private static Part Neighbour(Part part, Part target)
        {
            for (Part p = target; p != null; p = p.parent)
            {
                if (p.parent == part)
                {
                    return p;
                }
            }
            return part.parent;
        }

        /// <summary>
        /// The parts of the vessel of robotic part <paramref name="part"/> reached through a neighbour that
        /// another of its rigidbodies than <paramref name="hostBody"/> holds.
        /// </summary>
        private static List<Part> OtherSide(Part part, BaseServo servo, Rigidbody hostBody)
        {
            List<Part> result = new List<Part>();
            foreach (Part child in part.children)
            {
                if (SideBody(part, servo, child) != hostBody)
                {
                    AddTree(child, result);
                }
            }
            if (part.parent != null && SideBody(part, servo, part.parent) != hostBody)
            {
                foreach (Part p in part.vessel.parts)
                {
                    if (!IsInTree(p, part))
                    {
                        result.Add(p);
                    }
                }
            }
            return result;
        }

        private static void AddTree(Part root, List<Part> parts)
        {
            parts.Add(root);
            foreach (Part child in root.children)
            {
                AddTree(child, parts);
            }
        }

        private static bool IsInTree(Part p, Part root)
        {
            for (; p != null; p = p.parent)
            {
                if (p == root)
                {
                    return true;
                }
            }
            return false;
        }

        private static string Name(Part p) => $"{p.partInfo?.name ?? p.name} ({p.persistentId})";
    }
}
