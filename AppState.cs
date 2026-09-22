using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NextCue;

public sealed class AppState
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Plan? ActivePlan { get; set; }

    public List<Plan> OngoingPlans { get; set; } = [];

    public Guid? CurrentPlanId { get; set; }

    public List<Plan> PlanHistory { get; set; } = [];

    public AppSettings Settings { get; set; } = new();
}

public sealed class Plan
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string OverallTask { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset LastAccessedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public PlanStatus Status { get; set; } = PlanStatus.Ongoing;

    public List<PlanStep> Steps { get; set; } = [];
}

public sealed class PlanStep
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Text { get; set; } = "";

    public bool IsCompleted { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class AppSettings
{
    public bool AlwaysOnTop { get; set; } = true;

    public bool IsCollapsed { get; set; }

    public bool StartupCollapsed { get; set; }

    public bool MinimizeToTrayOnClose { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public DockSide DockSide { get; set; } = DockSide.Right;

    public double WindowWidth { get; set; } = 310;

    public double ExpandedWidth { get; set; } = 310;

    public double ExpandedHeight { get; set; } = 700;

    public double? ExpandedLeft { get; set; }

    public double? ExpandedTop { get; set; }

    public double CompactWidth { get; set; } = 170;

    public double CompactHeight { get; set; } = 700;

    public double? CompactLeft { get; set; }

    public double? CompactTop { get; set; }
}

public enum PlanStatus
{
    Active,
    Ongoing,
    Completed,
    Ended
}

public enum DockSide
{
    Left,
    Right
}
