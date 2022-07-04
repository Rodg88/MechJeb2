﻿using System.Collections.Generic;
using UnityEngine;
using MechJebLib.PVG;
using static MechJebLib.Utils.Statics;

#nullable enable

namespace MuMech
{
    public enum PVGStatus { ENABLED, INITIALIZED, BURNING, COASTING, TERMINAL, TERMINAL_RCS, FINISHED };

    /// <summary>
    /// TODO:
    /// - relay stage information of the Solution back to the UI
    /// - draw trajectory on the map view
    /// - draw terminal orbit on the map view
    /// - color coasts + burns different on the map view
    /// </summary>
    public class MechJebModuleGuidanceController : ComputerModule
    {
        public MechJebModuleGuidanceController(MechJebCore core) : base(core) { }

        // these variables will persist even if Reset() completely blows away the solution, so that pitch+heading will still be stable
        // until a new solution is found.
        public double   Pitch;
        public double   Heading;
        public double   Tgo;
        public double   VGO;

        public Solution? Solution;

        public PVGStatus Status = PVGStatus.ENABLED;

        public override void OnModuleEnabled()
        {
            Status = PVGStatus.ENABLED;
            core.attitude.users.Add(this);
            core.thrust.users.Add(this);
            Solution        = null;
            _allowExecution = false;
        }

        public override void OnModuleDisabled()
        {
            core.attitude.attitudeDeactivate();
            if (!core.rssMode)
                core.thrust.ThrustOff();
            core.thrust.users.Remove(this);
            Status = PVGStatus.FINISHED;
        }

        private bool _allowExecution;
        
        // we wait until we get a signal to allow execution to start
        public void AssertStart(bool allow_execution = true)
        {
            _allowExecution = allow_execution;
        }

        public override void OnFixedUpdate()
        {
            update_pitch_and_heading();

            if ( !HighLogic.LoadedSceneIsFlight )
            {
                Debug.Log("MechJebModuleGuidanceController [BUG]: PVG enabled in non-flight mode.  How does this happen?");
                Done();
            }

            if ( !enabled || Status == PVGStatus.ENABLED)
                return;

            if ( Status == PVGStatus.FINISHED )
            {
                Done();
                return;
            }
            
            /* hardcoded 10 seconds of terminal guidance */
            if ( Tgo < 10 )
            {
                // drop out of warp for terminal guidance (smaller time ticks => more accuracy)
                core.warp.MinimumWarp();
                Status = PVGStatus.TERMINAL;
            }

            if ( Solution != null && IsTerminalGuidance() )
            {
                // We might have wonky transforms and have a tiny bit of fore RCS, so require at least 10% of the max RCS thrust to be
                // in the pointy direction (which should be "up" / y-axis per KSP/Unity semantics).
                bool hasRCS = vessel.hasEnabledRCSModules() && vesselState.rcsThrustAvailable.up > 0.1 * vesselState.rcsThrustAvailable.MaxMagnitude();

                // stopping one tick short is more accurate for rockets without RCS, but sometimes we overshoot with only one tick
                int ticks = 1;
                if (hasRCS)
                    ticks = 2;

                if (Status == PVGStatus.TERMINAL_RCS && !vessel.ActionGroups[KSPActionGroup.RCS])  // if someone manually disables RCS
                {
                    Done();
                    return;
                }

                // bit of a hack to predict velocity + position in the next tick or two
                // FIXME: what exactly does KSP do to integrate over timesteps?
                Vector3d a0 = vessel.acceleration_immediate;

                double dt = ticks * TimeWarp.fixedDeltaTime;
                Vector3d v1 = vesselState.orbitalVelocity + a0 * dt;
                Vector3d x1 = vesselState.orbitalPosition + vesselState.orbitalVelocity * dt + 0.5 * a0 * dt * dt;

                double current = Solution.TerminalGuidanceMetric(vesselState.orbitalPosition.WorldToV3(), vesselState.orbitalVelocity.WorldToV3());
                double future = Solution.TerminalGuidanceMetric(x1.WorldToV3(), v1.WorldToV3());

                // ensure that we're burning in a roughly forward direction -- no idea why, but we can get a few ticks of backwards "thrust" due to staging during terminal guidance
                double costhrustangle = Vector3d.Dot(vesselState.forward, (vessel.acceleration_immediate - vessel.graviticAcceleration).normalized );

                if ( future > current && costhrustangle > 0.5 )
                {
                    Debug.Log("future = " + future);
                    Debug.Log("current = " + current);
                    if ( hasRCS && Status == PVGStatus.TERMINAL )
                    {
                        Status = PVGStatus.TERMINAL_RCS;
                        if (!vessel.ActionGroups[KSPActionGroup.RCS])
                            vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
                    }
                    else
                    {
                        Done();
                        return;
                    }
                }
            }

            handle_throttle();

            DrawTrajetory();
        }

        /* is guidance usable? */
        public bool IsStable()
        {
            return IsNormal() || IsTerminalGuidance();
        }

        // either ENABLED and waiting for a Solution, or executing a solution "normally" (not terminal, not failed)
        public bool IsReady()
        {
            return Status == PVGStatus.ENABLED || IsNormal();
        }

        // not TERMINAL guidance or TERMINAL_RCS
        public bool IsNormal()
        {
            return Status == PVGStatus.INITIALIZED || Status == PVGStatus.BURNING || Status == PVGStatus.COASTING;
        }

        private bool IsCoasting()
        {
            return Status == PVGStatus.COASTING;
        }

        private bool IsBurning()
        {
            return Status == PVGStatus.BURNING;
        }

        public bool IsTerminalGuidance()
        {
            return Status == PVGStatus.TERMINAL || Status == PVGStatus.TERMINAL_RCS;
        }

        /* normal pre-states but not usefully converged */
        public bool IsInitializing()
        {
            return Status == PVGStatus.ENABLED || Status == PVGStatus.INITIALIZED;
        }
        

        
        private bool ShouldCoast(Solution solution)
        {
            return vessel.currentStage == solution.CoastingKSPStage && solution.Thrust(vesselState.time) == 0;
        }

        private void handle_throttle()
        {
            if ( Solution == null )
                return;

            if ( !_allowExecution )
                return;
            
            if ( Status == PVGStatus.TERMINAL_RCS )
            {
                RCSOn();
                return;
            }

            if ( ShouldCoast(Solution) )
            {
                // force RCS on at the state transition
                if ( !IsCoasting() )
                {
                    if (!vessel.ActionGroups[KSPActionGroup.RCS])
                        vessel.ActionGroups.SetGroup(KSPActionGroup.RCS, true);
                }

                if ( !IsTerminalGuidance() )
                {
                    Status = PVGStatus.COASTING;
                }

                // this turns off autostaging during the coast (which currently affects fairing separation)
                core.staging.autostageLimitInternal = vessel.currentStage;
                ThrustOff();
            }
            else
            {
                if ( !IsBurning() )
                    ThrustOn();

                if ( !IsTerminalGuidance() )
                {
                    Status = PVGStatus.BURNING;
                }
                
                core.staging.autostageLimitInternal = 0;
            }
        }

        /* extract pitch and heading off of iF to avoid continuously recomputing on every call */
        private void update_pitch_and_heading()
        {
            if (Solution == null)
                return;

            // if we're not flying yet, continuously update the t0 of the solution
            if ( vessel.situation == Vessel.Situations.LANDED || vessel.situation == Vessel.Situations.PRELAUNCH || vessel.situation == Vessel.Situations.SPLASHED )
                Solution.T0 = vesselState.time;

            if ( Status != PVGStatus.TERMINAL_RCS )
            {
                (double pitch, double heading) = Solution.PitchAndHeading(vesselState.time);
                Pitch                          = Rad2Deg(pitch);
                Heading                        = Rad2Deg(heading);
                Tgo                            = Solution.Tgo(vesselState.time);
                VGO                            = Solution.Vgo(vesselState.time);
            }
            /* else leave pitch and heading at the last values, also stop updating vgo/tgo */
        }

        private List<Vector3d> _trajectory = new List<Vector3d>();

        private void DrawTrajetory()
        {
            if (Solution == null)
                return;
            
            _trajectory.Clear();
            double dt = Solution.Tf - Solution.T0;
            
            for (int i = 0; i <= 20; i++)
            {
                double t = Solution.T0 + dt * i / 20.0;

                _trajectory.Add(Solution.R(t).V3ToWorld() + mainBody.position);
            }
            GLUtils.DrawPath(mainBody, _trajectory, Color.red, MapView.MapIsEnabled);
        }

        private void ThrustOn()
        {
            core.thrust.targetThrottle = 1.0F;
        }

        private void RCSOn()
        {
            core.thrust.ThrustOff();
            vessel.ctrlState.Z = -1.0F;
        }

        private void ThrustOff()
        {
            core.thrust.ThrustOff();
        }

        private void Done()
        {
            users.Clear();
            ThrustOff();
            Status = PVGStatus.FINISHED;
            enabled = false;
        }

        public void Reset()
        {
            Status   = PVGStatus.ENABLED;
            if (!MuUtils.PhysicsRunning()) core.warp.MinimumWarp();
        }

        public void SetSolution(Solution solution)
        {
            this.Solution = solution;
            if (Status == PVGStatus.ENABLED)
                Status        = PVGStatus.INITIALIZED;
        }
    }
}
