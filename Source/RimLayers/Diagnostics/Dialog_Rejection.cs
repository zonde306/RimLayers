using RimWorld;
using UnityEngine;
using Verse;

namespace RimLayers.Diagnostics;

/// <summary>
/// Native-style window explaining why RimLayers refused an action. Shows the reason, the mod
/// responsible when there is one, and a button that copies a ready-to-paste bug report.
/// </summary>
public sealed class Dialog_Rejection : Window
{
    private const float PreferredWidth = 680f;
    private const float FooterHeight = 36f;

    private readonly RejectionReport report;
    private readonly float height;
    private Vector2 scroll;
    private float bodyHeight;
    private float viewportHeight;

    public Dialog_Rejection(RejectionReport report)
    {
        this.report=report;
        forcePause=true;
        doCloseX=true;
        // The window draws its own footer buttons, so the native close button would land on top of
        // them. The X in the title bar and Escape still close it.
        doCloseButton=false;
        absorbInputAroundWindow=true;
        onlyOneOfTypeAllowed=true;
        // Sized once, from the same measurement the draw uses: a one-line rule refusal gets a small
        // box instead of a mostly empty one, and a long stack trace is capped and scrolls.
        height=Mathf.Clamp(MeasureHeight(PreferredWidth-Margin*2f),240f,UI.screenHeight-60f);
    }

    public override Vector2 InitialSize => new(PreferredWidth,height);

    /// <summary>The refusal this window explains. Used to keep a per-frame refusal from stacking windows.</summary>
    public RejectionReport Report => report;

    /// <summary>Height the drawn body needed, as of the last draw. Diagnostics only.</summary>
    internal float ContentHeight => bodyHeight;

    /// <summary>Height actually available to the body, as of the last draw. Diagnostics only.</summary>
    internal float ViewportHeight => viewportHeight;

    private static string T(string key) => ("RL_Reject_"+key).Translate();

    /// <summary>
    /// Write the ready-to-paste report to the system clipboard. Split out of the button so the
    /// acceptance scenario exercises the same code the button runs instead of a copy of it.
    /// </summary>
    public void CopyToClipboard()
    {
        GUIUtility.systemCopyBuffer=report.BuildClipboardText();
    }

    public override void DoWindowContents(Rect inRect)
    {
        // WordWrap is global state that other windows and mods flip; measuring and drawing must agree
        // or the exception block reserves one line and the wrapped text is clipped.
        bool wrap=Text.WordWrap;
        Text.WordWrap=true;
        try { DrawContents(inRect); }
        finally { Text.WordWrap=wrap; }
    }

    private void DrawContents(Rect inRect)
    {
        float y=0f;
        var font=Text.Font;
        Text.Font=GameFont.Medium;
        Widgets.Label(new Rect(0f,y,inRect.width,34f),T("Title"));
        Text.Font=font;
        y+=36f;

        // Headline: what was refused.
        Widgets.Label(new Rect(0f,y,inRect.width,26f),report.Headline);
        y+=28f;

        // Culprit line, only when another mod or an unknown foreign type is implicated.
        if(HasCulprit)
        {
            var box=new Rect(0f,y,inRect.width,44f);
            Widgets.DrawBoxSolid(box,new Color(.32f,.13f,.13f));
            Widgets.DrawBoxSolidWithOutline(box,new Color(0f,0f,0f,0f),new Color(.75f,.35f,.3f),1);
            Widgets.Label(new Rect(box.x+8f,box.y+4f,box.width-16f,20f),CulpritHeadline());
            string member=CulpritMember();
            if(!member.NullOrEmpty())
            {
                Text.Font=GameFont.Tiny;
                Widgets.Label(new Rect(box.x+8f,box.y+24f,box.width-16f,16f),member);
                Text.Font=font;
            }
            y+=52f;
        }

        // Scrollable body: reason, context, exception text.
        var body=new Rect(0f,y,inRect.width,Math.Max(80f,inRect.height-y-FooterHeight-8f));
        var view=new Rect(0f,0f,body.width-20f,Math.Max(body.height,bodyHeight));
        viewportHeight=body.height;
        Widgets.BeginScrollView(body,ref scroll,view);
        try
        {
            float by=0f;
            Text.Font=GameFont.Small;
            string reasonLabel=T("Reason")+": "+report.Reason;
            float reasonHeight=Text.CalcHeight(reasonLabel,view.width);
            Widgets.Label(new Rect(0f,by,view.width,reasonHeight),reasonLabel);
            by+=reasonHeight+8f;

            string? context=ContextLine();
            if(!context.NullOrEmpty())
            {
                Text.Font=GameFont.Tiny;
                float contextHeight=Text.CalcHeight(context,view.width);
                Widgets.Label(new Rect(0f,by,view.width,contextHeight),context);
                by+=contextHeight+8f;
            }

            if(!report.Detail.NullOrEmpty())
            {
                Text.Font=GameFont.Tiny;
                float detailHeight=Text.CalcHeight(report.Detail,view.width);
                Widgets.Label(new Rect(0f,by,view.width,detailHeight),report.Detail);
                by+=detailHeight+8f;
            }

            if(!report.Exception.NullOrEmpty())
            {
                Text.Font=GameFont.Tiny;
                Widgets.Label(new Rect(0f,by,view.width,18f),T("Exception"));
                by+=20f;
                // Measure at the width the text is drawn at: the inner rect is inset by 6 on each
                // side, and measuring the wider box would reserve one line too few. The label gets the
                // measured height and the box adds the padding around it, so nothing is clipped.
                float textWidth=view.width-12f;
                float exceptionHeight=Text.CalcHeight(report.Exception,textWidth);
                var rect=new Rect(0f,by,view.width,exceptionHeight+8f);
                Widgets.DrawBoxSolid(rect,new Color(.12f,.12f,.12f));
                Widgets.Label(new Rect(rect.x+6f,rect.y+4f,textWidth,exceptionHeight),report.Exception);
                by+=rect.height+8f;
            }
            Text.Font=font;
            bodyHeight=by;
        }
        finally
        {
            Text.Font=font;
            Widgets.EndScrollView();
        }

        DrawFooter(new Rect(0f,inRect.height-FooterHeight,inRect.width,FooterHeight));
    }

    private bool HasCulprit => !report.CulpritMod.NullOrEmpty()||!report.CulpritType.NullOrEmpty();

    private string CulpritHeadline() =>
        report.CulpritMod.NullOrEmpty()
            ? T("CulpritType")+": "+report.CulpritType
            : T("CulpritMod")+": "+report.CulpritMod;

    private string CulpritMember() =>
        report.CulpritField.NullOrEmpty()?"":(report.CulpritType??"")+"."+report.CulpritField;

    /// <summary>
    /// How tall the window has to be to show everything without scrolling. Measured with the same
    /// font and width the draw uses.
    /// </summary>
    private float MeasureHeight(float width)
    {
        var font=Text.Font;
        bool wrap=Text.WordWrap;
        Text.WordWrap=true;
        float inner=width-20f;
        float total=36f+28f;
        if(HasCulprit)total+=52f;
        try
        {
            Text.Font=GameFont.Small;
            total+=Text.CalcHeight(T("Reason")+": "+report.Reason,inner)+8f;
            string? context=ContextLine();
            if(!context.NullOrEmpty())
            {
                Text.Font=GameFont.Tiny;
                total+=Text.CalcHeight(context,inner)+8f;
            }
            if(!report.Detail.NullOrEmpty())
            {
                Text.Font=GameFont.Tiny;
                total+=Text.CalcHeight(report.Detail,inner)+8f;
            }
            if(!report.Exception.NullOrEmpty())
            {
                Text.Font=GameFont.Tiny;
                // Same inset and padding the draw applies, so the reserved height matches the drawn one.
                total+=20f+Text.CalcHeight(report.Exception,inner-12f)+8f+8f;
            }
        }
        finally { Text.Font=font; Text.WordWrap=wrap; }
        return total+FooterHeight+8f+Margin*2f;
    }

    private string? ContextLine()
    {
        var parts=new List<string>();
        if(!report.MapName.NullOrEmpty())parts.Add(T("Map")+": "+report.MapName);
        if(report.CellX.HasValue&&report.CellZ.HasValue)
            parts.Add(T("Cell")+": "+report.CellX.Value+", "+report.CellZ.Value);
        if(report.LayerElevation.HasValue)
            parts.Add(T("Elevation")+": "+report.LayerElevation.Value);
        if(!report.DefName.NullOrEmpty())parts.Add(T("Def")+": "+report.DefName);
        return parts.Count==0?null:string.Join("   ",parts);
    }

    private void DrawFooter(Rect rect)
    {
        const float gap=8f;
        float copyWidth=200f;
        float closeWidth=120f;
        float logWidth=150f;
        float x=rect.x;

        // Label stays put after a copy: swapping in "copied" would re-lay-out the footer and the
        // toast already confirms it.
        if(Widgets.ButtonText(new Rect(x,rect.y,copyWidth,30f),T("Copy")))
        {
            CopyToClipboard();
            Messages.Message(T("Copied"),MessageTypeDefOf.SilentInput,false);
        }
        x+=copyWidth+gap;

        if(Widgets.ButtonText(new Rect(x,rect.y,logWidth,30f),T("OpenLog")))
        {
            Find.WindowStack.Add(new LudeonTK.EditWindow_Log());
        }

        if(Widgets.ButtonText(new Rect(rect.xMax-closeWidth,rect.y,closeWidth,30f),T("Close")))
            Close();
    }
}
