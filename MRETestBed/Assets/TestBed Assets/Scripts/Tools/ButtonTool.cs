// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using Assets.Scripts.Behaviors;
using Assets.Scripts.User;
using UnityEngine;

namespace Assets.Scripts.Tools
{
    public class ButtonTool : TargetTool
    {
        protected override void UpdateTool(InputSource inputSource)
        {
            base.UpdateTool(inputSource);

            if (Target == null)
            {
                return;
            }

            if (Input.GetButtonDown("Fire1"))
            {
                var buttonBehavior = Target.GetBehavior<ButtonBehavior>();
                if (buttonBehavior != null)
                {
                    var mwUser = buttonBehavior.GetMWUnityUser(inputSource.UserGameObject);
                    if (mwUser != null)
                    {
                        buttonBehavior.Button.StartAction(mwUser);
                    }
                }
            }
            else if (Input.GetButtonUp("Fire1"))
            {
                var buttonBehavior = Target.GetBehavior<ButtonBehavior>();
                if (buttonBehavior != null)
                {
                    var mwUser = buttonBehavior.GetMWUnityUser(inputSource.UserGameObject);
                    if (mwUser != null)
                    {
                        buttonBehavior.Button.StopAction(mwUser);
                        buttonBehavior.Click.StartAction(mwUser);
                    }
                }
            }
        }

        protected override void OnTargetChanged(GameObject oldTarget, GameObject newTarget, InputSource inputSource)
        {
            base.OnTargetChanged(oldTarget, newTarget, inputSource);

            if (oldTarget != null)
            {
                var oldBehavior = oldTarget.GetBehavior<ButtonBehavior>();
                if (oldBehavior != null)
                {
                    var mwUser = oldBehavior.GetMWUnityUser(inputSource.UserGameObject);
                    if (mwUser != null)
                    {
                        oldBehavior.Hover.StopAction(mwUser);
                    }
                }
            }

            
            if (newTarget != null)
            {
                var newBehavior = newTarget.GetBehavior<ButtonBehavior>();
                if (newBehavior != null)
                {
                    var mwUser = newBehavior.GetMWUnityUser(inputSource.UserGameObject);
                    if (mwUser != null)
                    {
                        newBehavior.Hover.StartAction(mwUser);
                    }
                }
            }
        }
    }
}
