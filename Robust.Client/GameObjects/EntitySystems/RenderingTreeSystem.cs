using System.Collections.Generic;
using JetBrains.Annotations;
using Robust.Client.Physics;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;

namespace Robust.Client.GameObjects
{
    /// <summary>
    ///     Keeps track of <see cref="DynamicTree{T}"/>s for various rendering-related components.
    /// </summary>
    [UsedImplicitly]
    public sealed class RenderingTreeSystem : EntitySystem
    {
        internal const string LoggerSawmill = "rendertree";

        // Nullspace is not indexed. Keep that in mind.

        [Dependency] private readonly IMapManager _mapManager = default!;

        private readonly List<SpriteComponent> _spriteQueue = new();
        private readonly List<PointLightComponent> _lightQueue = new();

        private readonly HashSet<EntityUid> _checkedChildren = new();

        /// <summary>
        /// <see cref="CVars.MaxLightRadius"/>
        /// </summary>
        public float MaxLightRadius { get; private set; }

        internal IEnumerable<RenderingTreeComponent> GetRenderTrees(MapId mapId, Box2Rotated worldBounds)
        {
            if (mapId == MapId.Nullspace) yield break;

            foreach (var grid in _mapManager.FindGridsIntersecting(mapId, worldBounds))
            {
                var tempQualifier = grid.GridEntityId;
                yield return EntityManager.GetComponent<RenderingTreeComponent>(tempQualifier);
            }

            var tempQualifier1 = _mapManager.GetMapEntityId(mapId);
            yield return EntityManager.GetComponent<RenderingTreeComponent>(tempQualifier1);
        }

        internal IEnumerable<RenderingTreeComponent> GetRenderTrees(MapId mapId, Box2 worldAABB)
        {
            if (mapId == MapId.Nullspace) yield break;

            foreach (var grid in _mapManager.FindGridsIntersecting(mapId, worldAABB))
            {
                var tempQualifier = grid.GridEntityId;
                yield return EntityManager.GetComponent<RenderingTreeComponent>(tempQualifier);
            }

            var tempQualifier1 = _mapManager.GetMapEntityId(mapId);
            yield return EntityManager.GetComponent<RenderingTreeComponent>(tempQualifier1);
        }

        public override void Initialize()
        {
            base.Initialize();

            UpdatesBefore.Add(typeof(SpriteSystem));
            UpdatesAfter.Add(typeof(TransformSystem));
            UpdatesAfter.Add(typeof(PhysicsSystem));

            _mapManager.MapCreated += MapManagerOnMapCreated;
            _mapManager.OnGridCreated += MapManagerOnGridCreated;

            // Due to how recursion works, this must be done.
            SubscribeLocalEvent<MoveEvent>(AnythingMoved);

            SubscribeLocalEvent<SpriteComponent, EntMapIdChangedMessage>(SpriteMapChanged);
            SubscribeLocalEvent<SpriteComponent, EntParentChangedMessage>(SpriteParentChanged);
            SubscribeLocalEvent<SpriteComponent, ComponentRemove>(RemoveSprite);
            SubscribeLocalEvent<SpriteComponent, SpriteUpdateEvent>(HandleSpriteUpdate);

            SubscribeLocalEvent<PointLightComponent, EntMapIdChangedMessage>(LightMapChanged);
            SubscribeLocalEvent<PointLightComponent, EntParentChangedMessage>(LightParentChanged);
            SubscribeLocalEvent<PointLightComponent, PointLightRadiusChangedEvent>(PointLightRadiusChanged);
            SubscribeLocalEvent<PointLightComponent, PointLightUpdateEvent>(HandleLightUpdate);

            SubscribeLocalEvent<RenderingTreeComponent, ComponentInit>(OnTreeInit);
            SubscribeLocalEvent<RenderingTreeComponent, ComponentRemove>(OnTreeRemove);

            var configManager = IoCManager.Resolve<IConfigurationManager>();
            configManager.OnValueChanged(CVars.MaxLightRadius, value => MaxLightRadius = value, true);
        }

        private void OnTreeInit(EntityUid uid, RenderingTreeComponent component, ComponentInit args)
        {
            component.LightTree = new DynamicTree<PointLightComponent>(LightAabbFunc);
            component.SpriteTree = new DynamicTree<SpriteComponent>(SpriteAabbFunc);
        }

        private void HandleLightUpdate(EntityUid uid, PointLightComponent component, PointLightUpdateEvent args)
        {
            if (component.TreeUpdateQueued) return;
            QueueLightUpdate(component);
        }

        private void HandleSpriteUpdate(EntityUid uid, SpriteComponent component, SpriteUpdateEvent args)
        {
            if (component.TreeUpdateQueued) return;
            QueueSpriteUpdate(component);
        }

        private void AnythingMoved(ref MoveEvent args)
        {
            var xforms = EntityManager.GetEntityQuery<TransformComponent>();

            AnythingMovedSubHandler(args.Sender, xforms);
        }

        private void AnythingMovedSubHandler(EntityUid uid, EntityQuery<TransformComponent> xforms)
        {
            // To avoid doing redundant updates (and we don't need to update a grid's children ever)
            if (!_checkedChildren.Add(uid) || EntityManager.HasComponent<RenderingTreeComponent>(uid)) return;

            // This recursive search is needed, as MoveEvent is defined to not care about indirect events like children.
            // WHATEVER YOU DO, DON'T REPLACE THIS WITH SPAMMING EVENTS UNLESS YOU HAVE A GUARANTEE IT WON'T LAG THE GC.
            // (Struct-based events ok though)
            // Ironically this was lagging the GC lolz
            if (EntityManager.TryGetComponent(uid, out SpriteComponent? sprite))
                QueueSpriteUpdate(sprite);

            if (EntityManager.TryGetComponent(uid, out PointLightComponent? light))
                QueueLightUpdate(light);

            if (!xforms.TryGetComponent(uid, out var xform)) return;

            foreach (var child in xform.ChildEntities)
            {
                AnythingMovedSubHandler(child, xforms);
            }
        }

        // For the RemoveX methods
        // If the Transform is removed BEFORE the Sprite/Light,
        // then the MapIdChanged code will handle and remove it (because MapId gets set to nullspace).
        // Otherwise these will still have their past MapId and that's all we need..

        #region SpriteHandlers
        private void SpriteMapChanged(EntityUid uid, SpriteComponent component, EntMapIdChangedMessage args)
        {
            QueueSpriteUpdate(component);
        }

        private void SpriteParentChanged(EntityUid uid, SpriteComponent component, ref EntParentChangedMessage args)
        {
            QueueSpriteUpdate(component);
        }

        private void RemoveSprite(EntityUid uid, SpriteComponent component, ComponentRemove args)
        {
            ClearSprite(component);
        }

        private void ClearSprite(SpriteComponent component)
        {
            if (component.RenderTree == null) return;

            component.RenderTree.SpriteTree.Remove(component);
            component.RenderTree = null;
        }

        private void QueueSpriteUpdate(SpriteComponent component)
        {
            if (component.TreeUpdateQueued) return;

            component.TreeUpdateQueued = true;
            _spriteQueue.Add(component);
        }
        #endregion

        #region LightHandlers
        private void LightMapChanged(EntityUid uid, PointLightComponent component, EntMapIdChangedMessage args)
        {
            QueueLightUpdate(component);
        }

        private void LightParentChanged(EntityUid uid, PointLightComponent component, ref EntParentChangedMessage args)
        {
            QueueLightUpdate(component);
        }

        private void PointLightRadiusChanged(EntityUid uid, PointLightComponent component, PointLightRadiusChangedEvent args)
        {
            QueueLightUpdate(component);
        }

        public void ClearLight(PointLightComponent component)
        {
            if (component.RenderTree == null) return;

            component.RenderTree.LightTree.Remove(component);
            component.RenderTree = null;
        }

        private void QueueLightUpdate(PointLightComponent component)
        {
            if (component.TreeUpdateQueued) return;

            component.TreeUpdateQueued = true;
            _lightQueue.Add(component);
        }
        #endregion

        public override void Shutdown()
        {
            base.Shutdown();
            _mapManager.MapCreated -= MapManagerOnMapCreated;
            _mapManager.OnGridCreated -= MapManagerOnGridCreated;
        }

        private void OnTreeRemove(EntityUid uid, RenderingTreeComponent component, ComponentRemove args)
        {
            foreach (var sprite in component.SpriteTree)
            {
                sprite.RenderTree = null;
            }

            foreach (var light in component.LightTree)
            {
                light.RenderTree = null;
            }

            component.SpriteTree.Clear();
            component.LightTree.Clear();
        }

        private void MapManagerOnMapCreated(object? sender, MapEventArgs e)
        {
            if (e.Map == MapId.Nullspace)
            {
                return;
            }

            EntityManager.EnsureComponent<RenderingTreeComponent>(_mapManager.GetMapEntityId(e.Map));
        }

        private void MapManagerOnGridCreated(MapId mapId, GridId gridId)
        {
            EntityManager.EnsureComponent<RenderingTreeComponent>(_mapManager.GetGrid(gridId).GridEntityId);
        }

        private RenderingTreeComponent? GetRenderTree(EntityUid entity, EntityQuery<TransformComponent> xforms)
        {
            var lookups = EntityManager.GetEntityQuery<RenderingTreeComponent>();

            if (!EntityManager.EntityExists(entity) ||
                !xforms.TryGetComponent(entity, out var xform) ||
                xform.MapID == MapId.Nullspace ||
                lookups.HasComponent(entity)) return null;

            var parent = xform.ParentUid;

            while (parent.IsValid())
            {
                if (lookups.TryGetComponent(parent, out var comp)) return comp;
                parent = xforms.GetComponent(parent).ParentUid;
            }

            return null;
        }

        private bool IsVisible(SpriteComponent component)
        {
            return component.Visible && !component.ContainerOccluded && !component.Deleted;
        }

        public override void FrameUpdate(float frameTime)
        {
            _checkedChildren.Clear();

            var xforms = EntityManager.GetEntityQuery<TransformComponent>();

            foreach (var sprite in _spriteQueue)
            {
                sprite.TreeUpdateQueued = false;
                if (!IsVisible(sprite))
                {
                    ClearSprite(sprite);
                    continue;
                }

                var oldMapTree = sprite.RenderTree;
                var newMapTree = GetRenderTree(sprite.Owner, xforms);
                // TODO: Temp PVS guard
                var xform = xforms.GetComponent(sprite.Owner);
                var (worldPos, worldRot) = xform.GetWorldPositionRotation();

                if (float.IsNaN(worldPos.X) || float.IsNaN(worldPos.Y))
                {
                    ClearSprite(sprite);
                    continue;
                }

                var aabb = SpriteAabbFunc(sprite, worldPos, worldRot, xforms);

                // If we're on a new map then clear the old one.
                if (oldMapTree != newMapTree)
                {
                    ClearSprite(sprite);
                    newMapTree?.SpriteTree.Add(sprite, aabb);
                }
                else
                {
                    newMapTree?.SpriteTree.Update(sprite, aabb);
                }

                sprite.RenderTree = newMapTree;
            }

            foreach (var light in _lightQueue)
            {
                light.TreeUpdateQueued = false;

                if (light.Deleted || !light.Enabled || light.ContainerOccluded)
                {
                    ClearLight(light);
                    continue;
                }

                var oldMapTree = light.RenderTree;
                var newMapTree = GetRenderTree(light.Owner, xforms);
                // TODO: Temp PVS guard
                var worldPos = xforms.GetComponent(light.Owner).WorldPosition;

                if (float.IsNaN(worldPos.X) || float.IsNaN(worldPos.Y))
                {
                    ClearLight(light);
                    continue;
                }

                // TODO: Events need a bit of cleanup so we only validate this on initialize and radius changed events
                // this is fine for now IMO as it's 1 float check for every light that moves
                if (light.Radius > MaxLightRadius)
                {
                    Logger.WarningS(LoggerSawmill, $"Light radius for {light.Owner} set above max radius of {MaxLightRadius}. This may lead to pop-in.");
                }

                var aabb = LightAabbFunc(light, worldPos, xforms);

                // If we're on a new map then clear the old one.
                if (oldMapTree != newMapTree)
                {
                    ClearLight(light);
                    newMapTree?.LightTree.Add(light, aabb);
                }
                else
                {
                    newMapTree?.LightTree.Update(light, aabb);
                }

                light.RenderTree = newMapTree;
            }

            _spriteQueue.Clear();
            _lightQueue.Clear();
        }

        private Box2 SpriteAabbFunc(in SpriteComponent value)
        {
            var xforms = EntityManager.GetEntityQuery<TransformComponent>();

            var xform = xforms.GetComponent(value.Owner);
            var (worldPos, worldRot) = xform.GetWorldPositionRotation();
            var bounds = new Box2Rotated(value.CalculateBoundingBox(worldPos), worldRot, worldPos);
            var tree = GetRenderTree(value.Owner, xforms);

            return tree == null ? bounds.CalcBoundingBox() : xforms.GetComponent(tree.Owner).InvWorldMatrix.TransformBox(bounds);
        }

        private Box2 LightAabbFunc(in PointLightComponent value)
        {
            var xforms = EntityManager.GetEntityQuery<TransformComponent>();

            var worldPos = xforms.GetComponent(value.Owner).WorldPosition;
            var tree = GetRenderTree(value.Owner, xforms);
            var boxSize = value.Radius * 2;

            var localPos = tree == null ? worldPos : xforms.GetComponent(tree.Owner).InvWorldMatrix.Transform(worldPos);
            return Box2.CenteredAround(localPos, (boxSize, boxSize));
        }

        private Box2 SpriteAabbFunc(SpriteComponent value, Vector2 worldPos, Angle worldRot, EntityQuery<TransformComponent> xforms)
        {
            var bounds = new Box2Rotated(value.CalculateBoundingBox(worldPos), worldRot, worldPos);
            var tree = GetRenderTree(value.Owner, xforms);

            return tree == null ? bounds.CalcBoundingBox() : xforms.GetComponent(tree.Owner).InvWorldMatrix.TransformBox(bounds);
        }

        private Box2 LightAabbFunc(PointLightComponent value, Vector2 worldPos, EntityQuery<TransformComponent> xforms)
        {
            // Lights are circles so don't need entity's rotation
            var tree = GetRenderTree(value.Owner, xforms);
            var boxSize = value.Radius * 2;

            var localPos = tree == null ? worldPos : xforms.GetComponent(tree.Owner).InvWorldMatrix.Transform(worldPos);
            return Box2.CenteredAround(localPos, (boxSize, boxSize));
        }
    }
}
