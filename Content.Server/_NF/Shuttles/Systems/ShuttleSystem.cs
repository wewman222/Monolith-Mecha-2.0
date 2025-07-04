// SPDX-FileCopyrightText: 2024 Whatstone
// SPDX-FileCopyrightText: 2024 neuPanda
// SPDX-FileCopyrightText: 2025 Ark
// SPDX-FileCopyrightText: 2025 Dvir
// SPDX-FileCopyrightText: 2025 Redrover1760
//
// SPDX-License-Identifier: AGPL-3.0-or-later

// New Frontiers - This file is licensed under AGPLv3
// Copyright (c) 2024 New Frontiers Contributors
// See AGPLv3.txt for details.
using Content.Server._NF.Station.Components;
using Content.Server.Shuttles.Components;
using Content.Shared._NF.Shuttles.Events;
using Content.Shared._NF.Shipyard.Components;
using Content.Server._Mono.Shuttles.Components;
using Robust.Shared.Physics.Components;

namespace Content.Server.Shuttles.Systems;

public sealed partial class ShuttleSystem
{
    private const float SpaceFrictionStrength = 0.0015f;
    private const float AnchorDampeningStrength = 0.5f;
    private void NfInitialize()
    {
        SubscribeLocalEvent<ShuttleConsoleComponent, SetInertiaDampeningRequest>(OnSetInertiaDampening);
        SubscribeLocalEvent<ShuttleConsoleComponent, SetMaxShuttleSpeedRequest>(OnSetMaxShuttleSpeed);
    }

    private bool SetInertiaDampening(EntityUid uid, PhysicsComponent physicsComponent, ShuttleComponent shuttleComponent, TransformComponent transform, InertiaDampeningMode mode)
    {
        if (!transform.GridUid.HasValue)
        {
            return false;
        }

        if (mode == InertiaDampeningMode.Query)
        {
            _console.RefreshShuttleConsoles(transform.GridUid.Value);
            return false;
        }

        if (!EntityManager.HasComponent<ShuttleDeedComponent>(transform.GridUid) &
            !EntityManager.HasComponent<DeedlessShuttleComponent>(transform.GridUid) || // Mono
            EntityManager.HasComponent<StationDampeningComponent>(_station.GetOwningStation(transform.GridUid)))
        {
            return false;
        }

        var linearDampeningStrength = mode switch
        {
            InertiaDampeningMode.Off => SpaceFrictionStrength,
            InertiaDampeningMode.Dampen => shuttleComponent.LinearDamping,
            InertiaDampeningMode.Anchor => AnchorDampeningStrength,
            _ => shuttleComponent.LinearDamping, // other values: default to some sane behaviour (assume normal dampening)
        };

        var angularDampeningStrength = mode switch
        {
            InertiaDampeningMode.Off => SpaceFrictionStrength,
            InertiaDampeningMode.Dampen => shuttleComponent.AngularDamping,
            InertiaDampeningMode.Anchor => AnchorDampeningStrength,
            _ => shuttleComponent.AngularDamping, // other values: default to some sane behaviour (assume normal dampening)
        };

        _physics.SetLinearDamping(transform.GridUid.Value, physicsComponent, linearDampeningStrength);
        _physics.SetAngularDamping(transform.GridUid.Value, physicsComponent, angularDampeningStrength);
        _console.RefreshShuttleConsoles(transform.GridUid.Value);
        return true;
    }

    private void OnSetInertiaDampening(EntityUid uid, ShuttleConsoleComponent component, SetInertiaDampeningRequest args)
    {
        // Ensure that the entity requested is a valid shuttle (stations should not be togglable)
        if (!EntityManager.TryGetComponent(uid, out TransformComponent? transform) ||
            !transform.GridUid.HasValue ||
            !EntityManager.TryGetComponent(transform.GridUid, out PhysicsComponent? physicsComponent) ||
            !EntityManager.TryGetComponent(transform.GridUid, out ShuttleComponent? shuttleComponent))
        {
            return;
        }

        if (SetInertiaDampening(uid, physicsComponent, shuttleComponent, transform, args.Mode) && args.Mode != InertiaDampeningMode.Query)
            component.DampeningMode = args.Mode;
    }

    private void OnSetMaxShuttleSpeed(EntityUid uid, ShuttleConsoleComponent component, SetMaxShuttleSpeedRequest args)
    {
        // Ensure that the entity requested is a valid shuttle
        if (!EntityManager.TryGetComponent(uid, out TransformComponent? transform) ||
            !transform.GridUid.HasValue ||
            !EntityManager.TryGetComponent(transform.GridUid, out ShuttleComponent? shuttleComponent))
        {
            return;
        }

        // Clamp the speed between 0 and 60
        // TODO: Make this account for thruster upgrades
        var maxSpeed = Math.Clamp(args.MaxSpeed, 0f, 60f);

        // Don't do anything if the value didn't change
        if (Math.Abs(shuttleComponent.BaseMaxLinearVelocity - maxSpeed) < 0.01f)
            return;

        shuttleComponent.BaseMaxLinearVelocity = maxSpeed;

        // Refresh the shuttle consoles to update the UI
        _console.RefreshShuttleConsoles(transform.GridUid.Value);
    }

    public InertiaDampeningMode NfGetInertiaDampeningMode(EntityUid entity)
    {
        if (!EntityManager.TryGetComponent<TransformComponent>(entity, out var xform))
            return InertiaDampeningMode.Dampen;

        // Not a shuttle, shouldn't be togglable // Mono - Added DeedlessShuttle
        if (!EntityManager.HasComponent<ShuttleDeedComponent>(xform.GridUid) &
            !EntityManager.HasComponent<DeedlessShuttleComponent>(xform.GridUid) ||
            EntityManager.HasComponent<StationDampeningComponent>(_station.GetOwningStation(xform.GridUid)))
            return InertiaDampeningMode.Station;

        if (!EntityManager.TryGetComponent(xform.GridUid, out PhysicsComponent? physicsComponent))
            return InertiaDampeningMode.Dampen;

        if (physicsComponent.LinearDamping >= AnchorDampeningStrength)
            return InertiaDampeningMode.Anchor;
        else if (physicsComponent.LinearDamping <= SpaceFrictionStrength)
            return InertiaDampeningMode.Off;
        else
            return InertiaDampeningMode.Dampen;
    }

    public void NfSetPowered(EntityUid uid, ShuttleConsoleComponent component, bool powered)
    {
        // Ensure that the entity requested is a valid shuttle (stations should not be togglable)
        if (!EntityManager.TryGetComponent(uid, out TransformComponent? transform) ||
            !transform.GridUid.HasValue ||
            !EntityManager.TryGetComponent(transform.GridUid, out PhysicsComponent? physicsComponent) ||
            !EntityManager.TryGetComponent(transform.GridUid, out ShuttleComponent? shuttleComponent))
        {
            return;
        }

        // Update dampening physics without adjusting requested mode.
        if (!powered)
        {
            SetInertiaDampening(uid, physicsComponent, shuttleComponent, transform, InertiaDampeningMode.Anchor);
        }
        else
        {
            // Update our dampening mode if we need to, and if we aren't a station.
            var currentDampening = NfGetInertiaDampeningMode(uid);
            if (currentDampening != component.DampeningMode &&
                currentDampening != InertiaDampeningMode.Station &&
                component.DampeningMode != InertiaDampeningMode.Station)
            {
                SetInertiaDampening(uid, physicsComponent, shuttleComponent, transform, component.DampeningMode);
            }
        }
    }

}
