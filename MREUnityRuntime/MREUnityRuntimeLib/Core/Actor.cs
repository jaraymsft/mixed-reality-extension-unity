// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using MixedRealityExtension.API;
using MixedRealityExtension.App;
using MixedRealityExtension.Behaviors;
using MixedRealityExtension.Core.Components;
using MixedRealityExtension.Core.Interfaces;
using MixedRealityExtension.Core.Types;
using MixedRealityExtension.Messaging.Commands;
using MixedRealityExtension.Messaging.Events.Types;
using MixedRealityExtension.Messaging.Payloads;
using MixedRealityExtension.Patching;
using MixedRealityExtension.Patching.Types;
using MixedRealityExtension.Util.Unity;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

using UnityLight = UnityEngine.Light;
using UnityCollider = UnityEngine.Collider;
using MixedRealityExtension.PluginInterfaces.Behaviors;
using MixedRealityExtension.Util;

namespace MixedRealityExtension.Core
{
    /// <summary>
    /// Class that represents an actor in a mixed reality extension app.
    /// </summary>
    internal sealed class Actor : MixedRealityExtensionObject, ICommandHandlerContext, IActor
    {
        private Rigidbody _rigidbody;
        private UnityLight _light;
        private UnityCollider _collider;
        private LookAtComponent _lookAt;
        private Dictionary<Guid, AudioSource> _soundInstances;
        private float _nextUpdateTime;
        private bool _grabbedLastSync = false;

        private MWScaledTransform _localTransform;
        private MWTransform _appTransform;

        private TransformLerper _transformLerper;

        private Dictionary<Type, ActorComponentBase> _components = new Dictionary<Type, ActorComponentBase>();

        private Queue<Action<Actor>> _updateActions = new Queue<Action<Actor>>();

        private ActorComponentType _subscriptions = ActorComponentType.None;

        private ActorTransformPatch _rbTransformPatch;

        private new Renderer renderer = null;
        internal Renderer Renderer => renderer = renderer ?? GetComponent<Renderer>();

        #region IActor Properties - Public

        /// <inheritdoc />
        [HideInInspector]
        public IActor Parent => App.FindActor(ParentId);

        /// <inheritdoc />
        [HideInInspector]
        public new string Name
        {
            get => transform.name;
            set => transform.name = value;
        }

        /// <inheritdoc />
        IMixedRealityExtensionApp IActor.App => base.App;

        /// <inheritdoc />
        [HideInInspector]
        public MWScaledTransform LocalTransform
        {
            get
            {
                _localTransform = _localTransform ?? transform.ToLocalTransform();
                return _localTransform;
            }

            private set
            {
                _localTransform = value;
            }
        }

        /// <inheritdoc />
        [HideInInspector]
        public MWTransform AppTransform
        {
            get
            {
                _appTransform = _appTransform ?? transform.ToAppTransform(App.SceneRoot.transform);
                return _appTransform;
            }

            private set
            {
                _appTransform = value;
            }
        }

        #endregion

        #region Properties - Internal

        internal Guid ParentId { get; set; } = Guid.Empty;

        internal RigidBody RigidBody { get; private set; }

        internal Light Light { get; private set; }

        internal IText Text { get; private set; }

        internal Collider Collider { get; private set; }

        internal Attachment Attachment { get; } = new Attachment();
        private Attachment _cachedAttachment = new Attachment();

        internal Guid MaterialId { get; set; } = Guid.Empty;
        
        internal bool Grabbable { get; private set; }

        internal bool IsGrabbed
        {
            get
            {
                var behaviorComponent = GetActorComponent<BehaviorComponent>();
                if (behaviorComponent != null && behaviorComponent.Behavior is ITargetBehavior targetBehavior)
                {
                    return targetBehavior.IsGrabbed;
                }

                return false;
            }
        }
        
        internal UInt32 appearanceEnabled = UInt32.MaxValue;
        internal bool activeAndEnabled =>
            ((Parent as Actor)?.activeAndEnabled ?? true)
            && ((App.LocalUser?.Groups ?? 1) & appearanceEnabled) > 0;

        #endregion

        #region Methods - Internal

        internal ComponentT GetActorComponent<ComponentT>() where ComponentT : ActorComponentBase
        {
            if (_components.ContainsKey(typeof(ComponentT)))
            {
                return (ComponentT)_components[typeof(ComponentT)];
            }

            return null;
        }

        internal ComponentT GetOrCreateActorComponent<ComponentT>() where ComponentT : ActorComponentBase, new()
        {
            var component = GetActorComponent<ComponentT>();
            if (component == null)
            {
                component = gameObject.AddComponent<ComponentT>();
                component.AttachedActor = this;
                _components[typeof(ComponentT)] = component;
            }

            return component;
        }

        internal void SynchronizeApp(ActorComponentType? subscriptionsOverride = null)
        {
            if (CanSync())
            {
                var subscriptions = subscriptionsOverride.HasValue ? subscriptionsOverride.Value : _subscriptions;

                // Handle changes in game state and raise appropriate events for network updates.
                var actorPatch = new ActorPatch(Id);

                // We need to detect for changes in parent on the client, and handle updating the server.
                // But only update if the identified parent is not pending.
                var parentId = Parent?.Id ?? Guid.Empty;
                if (ParentId != parentId && App.FindActor(ParentId) != null)
                {
                    // TODO @tombu - Determine if the new parent is an actor in OUR MRE.
                    // TODO: Add in MRE ID's to help identify whether the new parent is in our MRE or not, not just
                    // whether it is a MRE actor.
                    ParentId = parentId;
                    actorPatch.ParentId = ParentId;
                }

                if (ShouldSync(subscriptions, ActorComponentType.Transform))
                {
                    GenerateTransformPatch(actorPatch);
                }

                if (ShouldSync(subscriptions, ActorComponentType.Rigidbody))
                {
                    GenerateRigidBodyPatch(actorPatch);
                }

                if (ShouldSync(ActorComponentType.Attachment, ActorComponentType.Attachment))
                {
                    GenerateAttachmentPatch(actorPatch);
                }

                if (actorPatch.IsPatched())
                {
                    App.EventManager.QueueEvent(new ActorChangedEvent(Id, actorPatch));
                }

                // If the actor is grabbed or was grabbed last time we synced and is not grabbed any longer,
                // then we always need to sync the transform.
                if (IsGrabbed || _grabbedLastSync)
                {
                    var appTransform = transform.ToAppTransform(App.SceneRoot.transform);

                    var actorCorrection = new ActorCorrection()
                    {
                        ActorId = Id,
                        AppTransform = appTransform
                    };

                    App.EventManager.QueueEvent(new ActorCorrectionEvent(Id, actorCorrection));
                }

                // We update whether the actor was grabbed this sync to ensure we send one last transform update
                // on the sync when they are no longer grabbed.  This is the final transform update after the grab
                // is completed.  This should always be cached at the very end of the sync to ensure the value is valid
                // for any test calls to ShouldSync above.
                _grabbedLastSync = IsGrabbed;
            }
        }

        internal void ApplyPatch(ActorPatch actorPatch)
        {
            PatchName(actorPatch.Name);
            PatchParent(actorPatch.ParentId);
            PatchAppearance(actorPatch.Appearance);
            PatchTransform(actorPatch.Transform);
            PatchLight(actorPatch.Light);
            PatchRigidBody(actorPatch.RigidBody);
            PatchCollider(actorPatch.Collider);
            PatchText(actorPatch.Text);
            PatchAttachment(actorPatch.Attachment);
            PatchLookAt(actorPatch.LookAt);
            PatchGrabbable(actorPatch.Grabbable);
            PatchSubscriptions(actorPatch.Subscriptions);
        }

        internal void ApplyCorrection(ActorCorrection actorCorrection)
        {
            CorrectAppTransform(actorCorrection.AppTransform);
        }

        internal void SynchronizeEngine(ActorPatch actorPatch)
        {
            _updateActions.Enqueue((actor) => ApplyPatch(actorPatch));
        }

        internal void EngineCorrection(ActorCorrection actorCorrection)
        {
            _updateActions.Enqueue((actor) => ApplyCorrection(actorCorrection));
        }

        internal void ExecuteRigidBodyCommands(RigidBodyCommands commandPayload, Action onCompleteCallback)
        {
            foreach (var command in commandPayload.CommandPayloads.OfType<ICommandPayload>())
            {
                App.ExecuteCommandPayload(this, command, null);
            }
            onCompleteCallback?.Invoke();
        }

        internal void Destroy()
        {
            CleanUp();

            Destroy(gameObject);
        }

        internal ActorPatch GenerateInitialPatch()
        {
            LocalTransform = transform.ToLocalTransform();
            AppTransform = transform.ToAppTransform(App.SceneRoot.transform);

            var localTransform = new ScaledTransformPatch()
            {
                Position = new Vector3Patch(transform.localPosition),
                Rotation = new QuaternionPatch(transform.localRotation),
                Scale = new Vector3Patch(transform.localScale)
            };

            var appTransform = new TransformPatch()
            {
                Position = new Vector3Patch(App.SceneRoot.transform.InverseTransformPoint(transform.position)),
                Rotation = new QuaternionPatch(Quaternion.Inverse(App.SceneRoot.transform.rotation) * transform.rotation)
            };

            var rigidBody = PatchingUtilMethods.GeneratePatch(RigidBody, (Rigidbody)null, App.SceneRoot.transform);

            ColliderPatch collider = null;
            _collider = gameObject.GetComponent<UnityCollider>();
            if (_collider != null)
            {
                Collider = gameObject.AddComponent<Collider>();
                Collider.Initialize(_collider);
                collider = Collider.GenerateInitialPatch();
            }

            var actorPatch = new ActorPatch(Id)
            {
                ParentId = ParentId,
                Name = Name,
                Transform = new ActorTransformPatch()
                {
                    Local = localTransform,
                    App = appTransform
                },
                RigidBody = rigidBody,
                Collider = collider,
                Appearance = new AppearancePatch()
                {
                    Enabled = appearanceEnabled,
                    MaterialId = MaterialId
                }
            };

            return (!actorPatch.IsPatched()) ? null : actorPatch;
        }

        internal OperationResult EnableRigidBody(RigidBodyPatch rigidBodyPatch)
        {
            if (AddRigidBody() != null)
            {
                if (rigidBodyPatch != null)
                {
                    PatchRigidBody(rigidBodyPatch);
                }

                return new OperationResult()
                {
                    ResultCode = OperationResultCode.Success
                };
            }

            return new OperationResult()
            {
                ResultCode = OperationResultCode.Error,
                Message = string.Format("Failed to create and enable the rigidbody for actor with id {0}", Id)
            };
        }

        internal OperationResult EnableLight(LightPatch lightPatch)
        {
            if (AddLight() != null)
            {
                if (lightPatch != null)
                {
                    PatchLight(lightPatch);
                }

                return new OperationResult()
                {
                    ResultCode = OperationResultCode.Success
                };
            }

            return new OperationResult()
            {
                ResultCode = OperationResultCode.Error,
                Message = string.Format("Failed to create and enable the light for actor with id {0}", Id)
            };
        }

        internal OperationResult EnableText(TextPatch textPatch)
        {
            if (AddText() != null)
            {
                if (textPatch != null)
                {
                    PatchText(textPatch);
                }

                return new OperationResult()
                {
                    ResultCode = OperationResultCode.Success
                };
            }

            return new OperationResult()
            {
                ResultCode = OperationResultCode.Error,
                Message = string.Format("Failed to create and enable the text object for actor with id {0}", Id)
            };
        }

        internal void SendActorUpdate(ActorComponentType flags)
        {
            ActorPatch actorPatch = new ActorPatch(Id);

            if (flags.HasFlag(ActorComponentType.Transform))
            {
                actorPatch.Transform = new ActorTransformPatch()
                {
                    Local = transform.ToLocalTransform().AsPatch(),
                    App = transform.ToAppTransform(App.SceneRoot.transform).AsPatch()
                };
            }

            //if ((flags & SubscriptionType.Rigidbody) != SubscriptionType.None)
            //{
            //    actorPatch.Transform = this.RigidBody.AsPatch();
            //}

            if (actorPatch.IsPatched())
            {
                App.EventManager.QueueEvent(new ActorChangedEvent(Id, actorPatch));
            }
        }

        #endregion

        #region MonoBehaviour Virtual Methods

        protected override void OnStart()
        {
            _rigidbody = gameObject.GetComponent<Rigidbody>();
            _light = gameObject.GetComponent<UnityLight>();
        }

        protected override void OnDestroyed()
        {
            // TODO @tombu, @eanders - We need to decide on the correct cleanup timing here for multiplayer, as this could cause a potential
            // memory leak if the engine deletes game objects, and we don't do proper cleanup here.
            //CleanUp();
            //App.OnActorDestroyed(this.Id);

            IUserInfo userInfo = MREAPI.AppsAPI.UserInfoProvider.GetUserInfo(App, Attachment.UserId);
            if (userInfo != null)
            {
                userInfo.BeforeAvatarDestroyed -= UserInfo_BeforeAvatarDestroyed;
            }

            if (_soundInstances != null)
            {
                foreach (KeyValuePair<Guid, AudioSource> soundInstance in _soundInstances)
                {
                    App.SoundManager.DestroySoundInstance(soundInstance.Value, soundInstance.Key);
                }
            }
        }

        protected override void InternalUpdate()
        {
            try
            {
                while (_updateActions.Count > 0)
                {
                    _updateActions.Dequeue()(this);
                }

                // TODO: Add ability to flag an actor for "high-frequency" updates
                if (Time.time >= _nextUpdateTime)
                {
                    _nextUpdateTime = Time.time + 0.2f + UnityEngine.Random.Range(-0.1f, 0.1f);
                    SynchronizeApp();
                }
            }
            catch (Exception e)
            {
                MREAPI.Logger.LogError($"Failed to synchronize app.  Exception: {e.Message}\nStackTrace: {e.StackTrace}");
            }

            _transformLerper?.Update();
        }

        protected override void InternalFixedUpdate()
        {
            try
            {
                if (_rigidbody == null)
                {
                    return;
                }

                RigidBody = RigidBody ?? new RigidBody(_rigidbody, App.SceneRoot.transform);
                RigidBody.Update();
                // TODO: Send this update if actor is set to "high-frequency" updates
                //Actor.SynchronizeApp();
            }
            catch (Exception e)
            {
                MREAPI.Logger.LogError($"Failed to update rigid body.  Exception: {e.Message}\nStackTrace: {e.StackTrace}");
            }
        }

        #endregion

        #region Methods - Private

        private Attachment FindAttachmentInHierarchy()
        {
            Attachment FindAttachmentRecursive(Actor actor)
            {
                if (actor == null)
                {
                    return null;
                }
                if (actor.Attachment.AttachPoint != null && actor.Attachment.UserId != Guid.Empty)
                {
                    return actor.Attachment;
                }
                return FindAttachmentRecursive(actor.Parent as Actor);
            };
            return FindAttachmentRecursive(this);
        }

        private void DetachFromAttachPointParent()
        {
            try
            {
                if (transform != null)
                {
                    var attachmentComponent = transform.parent.GetComponents<MREAttachmentComponent>()
                        .FirstOrDefault(component =>
                            component.Actor != null &&
                            component.Actor.Id == Id &&
                            component.Actor.AppInstanceId == AppInstanceId &&
                            component.UserId == _cachedAttachment.UserId);

                    if (attachmentComponent != null)
                    {
                        attachmentComponent.Actor = null;
                        Destroy(attachmentComponent);
                    }

                    transform.SetParent(App.SceneRoot.transform, true);
                }
            }
            catch (Exception e)
            {
                MREAPI.Logger.LogError($"Exception: {e.Message}\nStackTrace: {e.StackTrace}");
            }
        }

        private bool PerformAttach()
        {
            // Assumption: Attachment state has changed and we need to (potentially) detach and (potentially) reattach.
            try
            {
                DetachFromAttachPointParent();

                IUserInfo userInfo = MREAPI.AppsAPI.UserInfoProvider.GetUserInfo(App, Attachment.UserId);
                if (userInfo != null)
                {
                    userInfo.BeforeAvatarDestroyed -= UserInfo_BeforeAvatarDestroyed;

                    Transform attachPoint = userInfo.GetAttachPoint(Attachment.AttachPoint);
                    if (attachPoint != null)
                    {
                        var attachmentComponent = attachPoint.gameObject.AddComponent<MREAttachmentComponent>();
                        attachmentComponent.Actor = this;
                        attachmentComponent.UserId = Attachment.UserId;
                        transform.SetParent(attachPoint, false);
                        userInfo.BeforeAvatarDestroyed += UserInfo_BeforeAvatarDestroyed;
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                MREAPI.Logger.LogError($"Exception: {e.Message}\nStackTrace: {e.StackTrace}");
            }

            return false;
        }

        private void UserInfo_BeforeAvatarDestroyed()
        {
            // Remember the original local transform.
            MWScaledTransform cachedTransform = LocalTransform;

            // Detach from parent. This will preserve the world transform (changing the local transform).
            // This is desired so that the actor doesn't change position, but we must restore the local
            // transform when reattaching.
            DetachFromAttachPointParent();

            IUserInfo userInfo = MREAPI.AppsAPI.UserInfoProvider.GetUserInfo(App, Attachment.UserId);
            if (userInfo != null)
            {
                void Reattach()
                {
                    // Restore the local transform and reattach.
                    userInfo.AfterAvatarCreated -= Reattach;
                    // In the interim time this actor might have been destroyed.
                    if (transform != null)
                    {
                        transform.localPosition = cachedTransform.Position.ToVector3();
                        transform.localRotation = cachedTransform.Rotation.ToQuaternion();
                        transform.localScale = cachedTransform.Scale.ToVector3();
                        PerformAttach();
                    }
                }

                // Register for a callback once the avatar is recreated.
                userInfo.AfterAvatarCreated += Reattach;
            }
        }

        private IText AddText()
        {
            Text = MREAPI.AppsAPI.TextFactory.CreateText(this);
            return Text;
        }

        private Light AddLight()
        {
            if (_light == null)
            {
                _light = gameObject.AddComponent<UnityLight>();
                Light = new Light(_light);
            }
            return Light;
        }

        private RigidBody AddRigidBody()
        {
            if (_rigidbody == null)
            {
                _rigidbody = gameObject.AddComponent<Rigidbody>();
                RigidBody = new RigidBody(_rigidbody, App.SceneRoot.transform);
            }
            return RigidBody;
        }

        private Collider SetCollider(ColliderPatch colliderPatch)
        {
            if (colliderPatch == null || colliderPatch.ColliderGeometry == null)
            {
                return null;
            }

            var colliderGeometry = colliderPatch.ColliderGeometry;
            var colliderType = colliderGeometry.ColliderType;

            if (_collider != null)
            {
                if (Collider.ColliderType == colliderType)
                {
                    // We have a collider already of the same type as the desired new geometry.
                    // Update its values instead of removing and adding a new one.
                    colliderGeometry.Patch(_collider);
                    return Collider;
                }
                else
                {
                    Destroy(_collider);
                    _collider = null;
                    Collider = null;
                }
            }

            UnityCollider unityCollider = null;

            switch (colliderType)
            {
                case ColliderType.Box:
                    var boxCollider = gameObject.AddComponent<BoxCollider>();
                    colliderGeometry.Patch(boxCollider);
                    unityCollider = boxCollider;
                    break;
                case ColliderType.Sphere:
                    var sphereCollider = gameObject.AddComponent<SphereCollider>();
                    colliderGeometry.Patch(sphereCollider);
                    unityCollider = sphereCollider;
                    break;
                default:
                    MREAPI.Logger.LogWarning("Cannot add the given collider type to the actor " +
                        $"during runtime.  Collider Type: {colliderPatch.ColliderGeometry.ColliderType}");
                    break;
            }

            _collider = unityCollider;
            Collider = (unityCollider != null) ? gameObject.AddComponent<Collider>() : null;
            Collider?.Initialize(_collider);
            return Collider;
        }

        private void PatchParent(Guid? parentId)
        {
            if (!parentId.HasValue)
            {
                return;
            }

            var newParent = App.FindActor(parentId.Value);
            if (parentId.Value != ParentId && parentId.Value == Guid.Empty)
            {
                // clear parent
                ParentId = Guid.Empty;
                transform.SetParent(App.SceneRoot.transform, false);
            }
            else if (parentId.Value != ParentId && newParent != null)
            {
                // reassign parent
                ParentId = parentId.Value;
                transform.SetParent(((Actor)newParent).transform, false);
            }
            else if (parentId.Value != ParentId)
            {
                // queue parent reassignment
                ParentId = parentId.Value;
                App.ProcessActorCommand(ParentId, new LocalCommand()
                {
                    Command = () =>
                    {
                        var freshParent = App.FindActor(ParentId) as Actor;
                        if (this != null && freshParent != null && transform.parent != freshParent.transform)
                        {
                            transform.SetParent(freshParent.transform, false);
                        }
                    }
                }, null);
            }
        }

        private void PatchName(string nameOrNull)
        {
            if (nameOrNull != null)
            {
                Name = nameOrNull;
                name = Name;
            }
        }

        private void PatchAppearance(AppearancePatch appearance)
        {
            if (appearance == null || Renderer == null)
            {
                return;
            }

            if (appearance.Enabled != null)
            {
                appearanceEnabled = appearance.Enabled.Value;
                ApplyVisibilityUpdate(this);
            }

            if (appearance.MaterialId == Guid.Empty)
            {
                Renderer.sharedMaterial = MREAPI.AppsAPI.DefaultMaterial;
            }
            else if (appearance.MaterialId != null)
            {
                MaterialId = appearance.MaterialId.Value;
                var sharedMat = MREAPI.AppsAPI.AssetCache.GetAsset(MaterialId) as Material;
                if (sharedMat != null)
                {
                    Renderer.sharedMaterial = sharedMat;
                }
                else
                {
                    MREAPI.Logger.LogWarning($"Material {MaterialId} not found, cannot assign to actor {Id}");
                }
            }
        }

        internal static void ApplyVisibilityUpdate(Actor actor, bool force = false)
        {
            // Note: MonoBehaviours don't support conditional access (actor.Renderer?.enabled)
            if (actor == null || actor.Renderer != null && actor.Renderer.enabled == actor.activeAndEnabled && !force)
            {
                return;
            }

            if (actor.Renderer != null)
            {
                actor.Renderer.enabled = actor.activeAndEnabled;
            }
            foreach (var child in actor.App.FindChildren(actor.Id))
            {
                ApplyVisibilityUpdate(child, force);
            }
        }

        private void PatchTransform(ActorTransformPatch transformPatch)
        {
            if (transformPatch != null)
            {
                if (RigidBody == null)
                {
                    // Apply local first.
                    if (transformPatch.Local != null)
                    {
                        transform.ApplyLocalPatch(LocalTransform, transformPatch.Local);
                    }

                    // Apply app patch second to ensure it overrides any duplicate values from the local patch.
                    // App transform patching always wins over local, except for scale.
                    if (transformPatch.App != null)
                    {
                        transform.ApplyAppPatch(App.SceneRoot.transform, AppTransform, transformPatch.App);
                    }
                }
                else
                {
                    PatchTransformWithRigidBody(transformPatch);   
                }
            }
        }

        private void PatchTransformWithRigidBody(ActorTransformPatch transformPatch)
        {
            if (_rigidbody == null)
            {
                return;
            }

            RigidBody.RigidBodyTransformUpdate transformUpdate = new RigidBody.RigidBodyTransformUpdate();
            if (transformPatch.Local != null)
            {
                // In case of rigid body:
                // - Apply scale directly.
                transform.localScale = transform.localScale.GetPatchApplied(LocalTransform.Scale.ApplyPatch(transformPatch.Local.Scale));

                // - Apply position and rotation via rigid body from local to world space.
                if (transformPatch.Local.Position != null)
                {
                    var localPosition = transform.localPosition.GetPatchApplied(LocalTransform.Position.ApplyPatch(transformPatch.Local.Position));
                    transformUpdate.Position = transform.TransformPoint(localPosition);
                }

                if (transformPatch.Local.Rotation != null)
                {
                    var localRotation = transform.localRotation.GetPatchApplied(LocalTransform.Rotation.ApplyPatch(transformPatch.Local.Rotation));
                    transformUpdate.Rotation = transform.rotation * localRotation;
                }
            }

            if (transformPatch.App != null)
            {
                var appTransform = App.SceneRoot.transform;

                if (transformPatch.App.Position != null)
                {
                    // New app space position.
                    var newAppPos = appTransform.InverseTransformPoint(transform.position)
                        .GetPatchApplied(AppTransform.Position.ApplyPatch(transformPatch.App.Position));

                    // Transform new position to world space.
                    transformUpdate.Position = appTransform.TransformPoint(newAppPos);
                }

                if (transformPatch.App.Rotation != null)
                {
                    // New app space rotation
                    var newAppRot = (transform.rotation * appTransform.rotation)
                        .GetPatchApplied(AppTransform.Rotation.ApplyPatch(transformPatch.App.Rotation));

                    // Transform new app rotation to world space.
                    transformUpdate.Rotation = newAppRot * transform.rotation;
                }
            }

            // Queue update to happen in the fixed update
            RigidBody.SynchronizeEngine(transformUpdate);
        }

        private void CorrectAppTransform(MWTransform transform)
        {
            if (transform == null)
            {
                return;
            }

            if (RigidBody == null)
            {
                // We need to lerp at the transform level with the transform lerper.
                if (_transformLerper == null)
                {
                    _transformLerper = new TransformLerper(gameObject.transform);
                }

                // Convert the app relative transform for the correction to world position relative to our app root.
                Vector3? newPos = null;
                Quaternion? newRot = null;

                if (transform.Position != null)
                {
                    Vector3 appPos;
                    appPos.x = transform.Position.X;
                    appPos.y = transform.Position.Y;
                    appPos.z = transform.Position.Z;
                    newPos = App.SceneRoot.transform.TransformPoint(appPos);
                }

                if (transform.Rotation != null)
                {
                    Quaternion appRot;
                    appRot.w = transform.Rotation.W;
                    appRot.x = transform.Rotation.X;
                    appRot.y = transform.Rotation.Y;
                    appRot.z = transform.Rotation.Z;
                    newRot = App.SceneRoot.transform.rotation * appRot;
                }

                // We do not pass in a value for the update period at this point.  We will be adding in lag
                // prediction for the network here in the future once that is more fully fleshed out.
                _transformLerper.SetTarget(newPos, newRot);
            }
            else
            {
                // Lerping and correction needs to happen at the rigid body level here to
                // not interfere with physics simulation.  This will change with kinematic being
                // enabled on a rigid body for when it is grabbed.  We do not support this currently,
                // and thus do not interpolate the actor.  Just set the position for the rigid body.

                _rbTransformPatch = _rbTransformPatch ?? new ActorTransformPatch()
                {
                    App = new TransformPatch()
                    {
                        Position = new Vector3Patch(),
                        Rotation = new QuaternionPatch()
                    }
                };

                if (transform.Position != null)
                {
                    _rbTransformPatch.App.Position.X = transform.Position.X;
                    _rbTransformPatch.App.Position.Y = transform.Position.Y;
                    _rbTransformPatch.App.Position.Z = transform.Position.Z;
                }
                else
                {
                    _rbTransformPatch.App.Position = null;
                }

                if (transform.Rotation != null)
                {
                    _rbTransformPatch.App.Rotation.W = transform.Rotation.W;
                    _rbTransformPatch.App.Rotation.X = transform.Rotation.X;
                    _rbTransformPatch.App.Rotation.Y = transform.Rotation.Y;
                    _rbTransformPatch.App.Rotation.Z = transform.Rotation.Z;
                }
                else
                {
                    _rbTransformPatch.App.Rotation = null;
                }

                PatchTransformWithRigidBody(_rbTransformPatch);
            }
        }

        private void PatchLight(LightPatch lightPatch)
        {
            if (lightPatch != null)
            {
                if (Light == null)
                {
                    AddLight();
                }
                Light.SynchronizeEngine(lightPatch);
            }
        }

        private void PatchRigidBody(RigidBodyPatch rigidBodyPatch)
        {
            if (rigidBodyPatch != null)
            {
                if (RigidBody == null)
                {
                    AddRigidBody();
                    RigidBody.ApplyPatch(rigidBodyPatch);
                }
                else
                {
                    // Queue update to happen in the fixed update
                    RigidBody.SynchronizeEngine(rigidBodyPatch);
                }
            }
        }

        private void PatchText(TextPatch textPatch)
        {
            if (textPatch != null)
            {
                if (Text == null)
                {
                    AddText();
                }
                Text.SynchronizeEngine(textPatch);
            }
        }

        private void PatchCollider(ColliderPatch colliderPatch)
        {
            if (colliderPatch != null)
            {
                // A collider patch that contains collider geometry signals that we need to update the
                // collider to match the desired geometry.
                if (colliderPatch.ColliderGeometry != null)
                {
                    SetCollider(colliderPatch);
                }

                Collider?.SynchronizeEngine(colliderPatch);
            }
        }

        private void PatchAttachment(AttachmentPatch attachmentPatch)
        {
            if (attachmentPatch != null && attachmentPatch.IsPatched() && !attachmentPatch.Equals(Attachment))
            {
                Attachment.ApplyPatch(attachmentPatch);
                if (!PerformAttach())
                {
                    Attachment.Clear();
                }
            }
        }

        private void PatchLookAt(LookAtPatch lookAtPatch)
        {
            if (lookAtPatch != null)
            {
                if (_lookAt == null)
                {
                    _lookAt = GetOrCreateActorComponent<LookAtComponent>();
                }
                _lookAt.ApplyPatch(lookAtPatch);
            }
        }

        private void PatchGrabbable(bool? grabbable)
        {
            if (grabbable != null && grabbable.Value != Grabbable)
            {
                // Update existing behavior or add a basic target behavior if there isn't one already.
                var behaviorComponent = GetActorComponent<BehaviorComponent>();
                if (behaviorComponent == null)
                {
                    // NOTE: We need to have the default behavior on an actor be a button for now in the case we want the actor
                    // to be able to be grabbed on all controller types for host apps.  This will be a base Target behavior once we
                    // update host apps to handle button conflicts.
                    behaviorComponent = GetOrCreateActorComponent<BehaviorComponent>();
                    var handler = BehaviorHandlerFactory.CreateBehaviorHandler(BehaviorType.Button, this, new WeakReference<MixedRealityExtensionApp>(App));
                    behaviorComponent.SetBehaviorHandler(handler);
                }

                ((ITargetBehavior)behaviorComponent.Behavior).Grabbable = grabbable.Value;

                Grabbable = grabbable.Value;
            }
        }

        private void PatchSubscriptions(IEnumerable<ActorComponentType> subscriptions)
        {
            if (subscriptions != null)
            {
                _subscriptions = ActorComponentType.None;
                foreach (var subscription in subscriptions)
                {
                    _subscriptions |= subscription;
                }
            }
        }

        private void GenerateTransformPatch(ActorPatch actorPatch)
        {
            var transformPatch = new ActorTransformPatch()
            {
                Local = PatchingUtilMethods.GenerateLocalTransformPatch(LocalTransform, transform),
                App = PatchingUtilMethods.GenerateAppTransformPatch(AppTransform, transform, App.SceneRoot.transform)
            };

            LocalTransform = transform.ToLocalTransform();
            AppTransform = transform.ToAppTransform(App.SceneRoot.transform);

            actorPatch.Transform = transformPatch.IsPatched() ? transformPatch : null;
        }

        private void GenerateRigidBodyPatch(ActorPatch actorPatch)
        {
            if (_rigidbody != null && RigidBody != null)
            {
                // convert to a RigidBody and build a patch from the old one to this one.
                var rigidBodyPatch = PatchingUtilMethods.GeneratePatch(RigidBody, _rigidbody, App.SceneRoot.transform);
                if (rigidBodyPatch != null && rigidBodyPatch.IsPatched())
                {
                    actorPatch.RigidBody = rigidBodyPatch;
                }

                RigidBody.Update(_rigidbody);
            }
        }

        private void GenerateAttachmentPatch(ActorPatch actorPatch)
        {
            actorPatch.Attachment = Attachment.GeneratePatch(_cachedAttachment);
            if (actorPatch.Attachment != null)
            {
                _cachedAttachment.CopyFrom(Attachment);
            }
        }

        private void CleanUp()
        {
            foreach (var component in _components.Values)
            {
                component.CleanUp();
            }
        }

        private bool ShouldSync(ActorComponentType subscriptions, ActorComponentType flag)
        {
            // We do not want to send actor updates until we're fully joined to the app.
            // TODO: We shouldn't need to do this check. The engine shouldn't try to send
            // updates until we're fully joined to the app.
            if (!(App.Protocol is Messaging.Protocols.Execution))
            {
                return false;
            }

            // If the actor has a rigid body then always sync the transform and the rigid body.
            if (RigidBody != null)
            {
                subscriptions |= ActorComponentType.Transform;
                subscriptions |= ActorComponentType.Rigidbody;
            }

            Attachment attachmentInHierarchy = FindAttachmentInHierarchy();
            bool inAttachmentHeirarchy = (attachmentInHierarchy != null);
            bool inOwnedAttachmentHierarchy = (inAttachmentHeirarchy && attachmentInHierarchy.UserId == LocalUser.Id);

            // Don't sync anything if the actor is in an attachment hierarchy on a remote avatar.
            if (inAttachmentHeirarchy && !inOwnedAttachmentHierarchy)
            {
                subscriptions = ActorComponentType.None;
            }

            if (subscriptions.HasFlag(flag))
            {
                return
                    ((App.OperatingModel == OperatingModel.ServerAuthoritative) ||
                    App.IsAuthoritativePeer ||
                    inOwnedAttachmentHierarchy) && !IsGrabbed;
            }

            return false;
        }

        private bool CanSync()
        {
            // We do not want to send actor updates until we're fully joined to the app.
            // TODO: We shouldn't need to do this check. The engine shouldn't try to send
            // updates until we're fully joined to the app.
            if (!(App.Protocol is Messaging.Protocols.Execution))
            {
                return false;
            }

            Attachment attachmentInHierarchy = FindAttachmentInHierarchy();
            bool inAttachmentHeirarchy = (attachmentInHierarchy != null);
            bool inOwnedAttachmentHierarchy = (inAttachmentHeirarchy && attachmentInHierarchy.UserId == LocalUser.Id);

            // We can send actor updates to the app if we're operating in a server-authoritative model,
            // or if we're in a peer-authoritative model and we've been designated the authoritative peer.
            // Override the previous rules if this actor is grabbed by the local user or is in an attachment
            // hierarchy owned by the local user.
            if (App.OperatingModel == OperatingModel.ServerAuthoritative ||
                App.IsAuthoritativePeer ||
                IsGrabbed ||
                _grabbedLastSync ||
                inOwnedAttachmentHierarchy)
            {
                return true;
            }

            return false;
        }

        #endregion

        #region Command Handlers

        [CommandHandler(typeof(LocalCommand))]
        private void OnLocalCommand(LocalCommand payload, Action onCompleteCallback)
        {
            payload.Command?.Invoke();
            onCompleteCallback?.Invoke();
        }

        [CommandHandler(typeof(ActorCorrection))]
        private void OnActorCorrection(ActorCorrection payload, Action onCompleteCallback)
        {
            EngineCorrection(payload);
            onCompleteCallback?.Invoke();
        }

        [CommandHandler(typeof(ActorUpdate))]
        private void OnActorUpdate(ActorUpdate payload, Action onCompleteCallback)
        {
            SynchronizeEngine(payload.Actor);
            onCompleteCallback?.Invoke();
        }

        [CommandHandler(typeof(RigidBodyCommands))]
        private void OnRigidBodyCommands(RigidBodyCommands payload, Action onCompleteCallback)
        {
            ExecuteRigidBodyCommands(payload, onCompleteCallback);
        }

        [CommandHandler(typeof(CreateAnimation))]
        private void OnCreateAnimation(CreateAnimation payload, Action onCompleteCallback)
        {
            GetOrCreateActorComponent<AnimationComponent>()
                .CreateAnimation(
                    payload.AnimationName,
                    payload.Keyframes,
                    payload.Events,
                    payload.WrapMode,
                    payload.InitialState,
                    isInternal: false,
                    onCreatedCallback: () => onCompleteCallback?.Invoke());
        }

        [CommandHandler(typeof(SetAnimationState))]
        private void OnSetAnimationState(SetAnimationState payload, Action onCompleteCallback)
        {
            GetOrCreateActorComponent<AnimationComponent>()
                .SetAnimationState(payload.AnimationName, payload.State.Time, payload.State.Speed, payload.State.Enabled);
            onCompleteCallback?.Invoke();
        }

        [CommandHandler(typeof(SetSoundState))]
        private void OnSetSoundState(SetSoundState payload, Action onCompleteCallback)
        {
            if (payload.SoundCommand == SoundCommand.Start)
            {
                AudioSource soundInstance = App.SoundManager.TryAddSoundInstance(this, payload.Id, payload.SoundAssetId, payload.Options, payload.StartTimeOffset);
                if (soundInstance)
                {
                    if (_soundInstances == null)
                    {
                        _soundInstances = new Dictionary<Guid, AudioSource>();
                    }
                    _soundInstances.Add(payload.Id, soundInstance);
                }
            }
            else
            {
                if (_soundInstances != null && _soundInstances.TryGetValue(payload.Id, out AudioSource soundInstance))
                {
                    switch (payload.SoundCommand)
                    {
                        case SoundCommand.Stop:
                            DestroySoundById(payload.Id, soundInstance);
                            break;
                        case SoundCommand.Update:
                            App.SoundManager.ApplySoundStateOptions(this, soundInstance, payload.Options, payload.Id, false);
                            break;
                    }
                }
            }
            onCompleteCallback?.Invoke();
        }

        public bool CheckIfSoundExpired(Guid id)
        {
            if (_soundInstances != null && _soundInstances.TryGetValue(id, out AudioSource soundInstance))
            {
                if (soundInstance.isPlaying)
                {
                    return false;
                }
                DestroySoundById(id, soundInstance);
            }
            return true;
        }

        private void DestroySoundById(Guid id, AudioSource soundInstance)
        {
            _soundInstances.Remove(id);
            App.SoundManager.DestroySoundInstance(soundInstance, id);
        }


        [CommandHandler(typeof(InterpolateActor))]
        private void OnInterpolateActor(InterpolateActor payload, Action onCompleteCallback)
        {
            GetOrCreateActorComponent<AnimationComponent>()
                .Interpolate(
                    payload.Value,
                    payload.AnimationName,
                    payload.Duration,
                    payload.Curve,
                    payload.Enabled);
            onCompleteCallback?.Invoke();
        }

        [CommandHandler(typeof(SetBehavior))]
        private void OnSetBehavior(SetBehavior payload, Action onCompleteCallback)
        {
            var behaviorComponent = GetOrCreateActorComponent<BehaviorComponent>();

            if (payload.BehaviorType == BehaviorType.None && behaviorComponent.ContainsBehaviorHandler())
            {
                behaviorComponent.ClearBehaviorHandler();
            }
            else
            {
                var handler = BehaviorHandlerFactory.CreateBehaviorHandler(payload.BehaviorType, this, new WeakReference<MixedRealityExtensionApp>(App));
                behaviorComponent.SetBehaviorHandler(handler);
            }
            onCompleteCallback?.Invoke();
        }

        #endregion

        #region Command Handlers - Rigid Body Commands

        [CommandHandler(typeof(RBMovePosition))]
        private void OnRBMovePosition(RBMovePosition payload, Action onCompleteCallback)
        {
            RigidBody?.RigidBodyMovePosition(new MWVector3().ApplyPatch(payload.Position));
            onCompleteCallback?.Invoke();
        }

        [CommandHandler(typeof(RBMoveRotation))]
        private void OnRBMoveRotation(RBMoveRotation payload, Action onCompleteCallback)
        {
            RigidBody?.RigidBodyMoveRotation(new MWQuaternion().ApplyPatch(payload.Rotation));
            onCompleteCallback?.Invoke();
        }

        [CommandHandler(typeof(RBAddForce))]
        private void OnRBAddForce(RBAddForce payload, Action onCompleteCallback)
        {
            RigidBody?.RigidBodyAddForce(new MWVector3().ApplyPatch(payload.Force));
            onCompleteCallback?.Invoke();
        }

        [CommandHandler(typeof(RBAddForceAtPosition))]
        private void OnRBAddForceAtPosition(RBAddForceAtPosition payload, Action onCompleteCallback)
        {
            var force = new MWVector3().ApplyPatch(payload.Force);
            var position = new MWVector3().ApplyPatch(payload.Position);
            RigidBody?.RigidBodyAddForceAtPosition(force, position);
            onCompleteCallback?.Invoke();
        }

        [CommandHandler(typeof(RBAddTorque))]
        private void OnRBAddTorque(RBAddTorque payload, Action onCompleteCallback)
        {
            RigidBody?.RigidBodyAddTorque(new MWVector3().ApplyPatch(payload.Torque));
            onCompleteCallback?.Invoke();
        }

        [CommandHandler(typeof(RBAddRelativeTorque))]
        private void OnRBAddRelativeTorque(RBAddRelativeTorque payload, Action onCompleteCallback)
        {
            RigidBody?.RigidBodyAddRelativeTorque(new MWVector3().ApplyPatch(payload.RelativeTorque));
            onCompleteCallback?.Invoke();
        }

        #endregion
    }
}
