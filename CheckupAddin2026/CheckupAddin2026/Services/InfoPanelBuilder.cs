using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CheckupAddIn.Services
{
    internal static class InfoPanelBuilder
    {
        // ─── public entry points ───────────────────────────────────────────

        public static UIElement BuildMainWindowHelp()
        {
            var p = NewPanel();
            AddL1Header(p, L("Info_Main_Title"));
            AddBullet(p, L("Info_Main_Intro"));
            AddBullet(p, L("Info_Main_Edit"));
            AddBullet(p, L("Info_Main_Formula"));
            AddBullet(p, L("Info_Main_RightClick"));
            AddBullet(p, L("Info_Main_Drag"));
            AddBullet(p, L("Info_Main_Preset"));
            AddBullet(p, L("Info_Main_Refresh"));
            return p;
        }

        public static UIElement BuildRoleHelp()
        {
            var p = NewPanel();
            AddL1Header(p, L("Info_Roles_Title"));
            AddRoleEntry(p, "—  None",                L("Info_Roles_None"));
            AddRoleEntry(p, "PRI  PrimaryDisplay",    L("Info_Roles_PRI"));
            AddRoleEntry(p, "SEC  SecondaryDisplay",  L("Info_Roles_SEC"));
            AddRoleEntry(p, "TAB  TabId",             L("Info_Roles_TAB"));
            AddRoleEntry(p, "GRP  GroupId",           L("Info_Roles_GRP"));
            AddRoleEntry(p, "SRT  SortKey",           L("Info_Roles_SRT"));
            AddRoleEntry(p, "GST  GroupSortKey",      L("Info_Roles_GST"));
            AddRoleEntry(p, "TST  TabSortKey",        L("Info_Roles_TST"));
            AddRoleEntry(p, "AUX  Auxiliary",         L("Info_Roles_AUX"));
            AddRule(p);
            AddPara(p, L("Info_Roles_RightClick"));
            AddRule(p);
            AddL2Header(p, L("Info_Roles_ExampleTitle"));
            AddCode(p, L("Info_Roles_ExampleColumns"));
            AddPara(p, L("Info_Roles_ExampleResult"), 12);
            AddRule(p);
            AddL2Header(p, L("Info_Roles_SearchTitle"));
            AddPara(p, L("Info_Roles_SearchBody"));
            return p;
        }

        public static UIElement BuildCardHelp()
        {
            var p = NewPanel();
            AddL1Header(p, L("Info_Cards_Title"));

            // Catalog tab
            AddL2Header(p, L("CatBuilder_Tab_Catalogs"));
            AddPara(p, L("Info_Cards_CatalogTabDesc"));
            AddL3Header(p, L("Info_Cards_InlineTitle"));
            AddPara(p, L("Info_Cards_InlineBody"), 12);

            AddRule(p);

            // Logic tab
            AddL2Header(p, L("CatBuilder_Tab_Capabilities"));
            AddPara(p, L("Info_Cards_LogicTabDesc"));
            AddL3Header(p, L("Info_Cards_LogicCardsHeader"));

            AddCardSection(p, L("CardType_Dropdown"),      L("Info_Cards_Dropdown_Desc"));
            AddCardSection(p, L("CardType_Sync"),          L("Info_Cards_Sync_Desc"));
            AddCardSection(p, L("CardType_PairTransform"), L("Info_Cards_PairTransform_Desc"));
            AddCardSection(p, L("CardType_Link"),          L("Info_Cards_Link_Desc"));
            AddCardSection(p, L("CardType_Button"),        L("Info_Cards_Button_Desc"));
            AddCardSection(p, L("CardType_SmartComplete"), L("Info_Cards_MultiPick_Desc"));
            AddCardSection(p, L("CardType_Search"),        L("Info_Cards_Search_Desc"));
            AddCardSection(p, L("CardType_PrefixSuffix"),  L("Info_Cards_PrefixSuffix_Desc"));
            AddCardSection(p, L("CardType_Sort"),          L("Info_Cards_Sort_Desc"));

            AddRule(p);

            // Basic Logics
            AddL2Header(p, L("Cap_BasicLogicsTitle"));
            AddPara(p, L("Info_Cards_BasicLogics_Desc"));
            AddCode(p, L("BasicLogic_Concatenate") + "  —  " + L("Tip_BasicLogic_Concatenate"));
            AddCode(p, L("BasicLogic_IfElse")      + "  —  " + L("Tip_BasicLogic_IfElse"));
            AddCode(p, L("BasicLogic_Lookup")      + "  —  " + L("Tip_BasicLogic_Lookup"));
            AddCode(p, L("BasicLogic_Format")      + "  —  " + L("Tip_BasicLogic_Format"));
            AddCode(p, L("BasicLogic_Round")       + "  —  " + L("Tip_BasicLogic_Round"));
            AddCode(p, L("BasicLogic_Value")       + "  —  " + L("Tip_BasicLogic_Value"));
            AddCode(p, L("BasicLogic_Eq")          + "  —  " + L("Tip_BasicLogic_Eq"));
            AddCode(p, L("BasicLogic_Ne")          + "  —  " + L("Tip_BasicLogic_Ne"));
            AddCode(p, L("BasicLogic_Lt")          + "  —  " + L("Tip_BasicLogic_Lt"));
            AddCode(p, L("BasicLogic_Gt")          + "  —  " + L("Tip_BasicLogic_Gt"));
            AddCode(p, L("BasicLogic_Lte")         + "  —  " + L("Tip_BasicLogic_Lte"));
            AddCode(p, L("BasicLogic_Gte")         + "  —  " + L("Tip_BasicLogic_Gte"));
            AddCode(p, L("BasicLogic_And")         + "  —  " + L("Tip_BasicLogic_And"));
            AddCode(p, L("BasicLogic_Or")          + "  —  " + L("Tip_BasicLogic_Or"));
            AddCode(p, L("BasicLogic_Not")         + "  —  " + L("Tip_BasicLogic_Not"));
            AddCode(p, L("BasicLogic_Str")         + "  —  " + L("Tip_BasicLogic_Str"));
            AddCode(p, L("BasicLogic_Join")        + "  —  " + L("Tip_BasicLogic_Join"));
            AddCode(p, L("BasicLogic_Left")        + "  —  " + L("Tip_BasicLogic_Left"));
            AddCode(p, L("BasicLogic_Right")       + "  —  " + L("Tip_BasicLogic_Right"));
            AddCode(p, L("BasicLogic_Mid")         + "  —  " + L("Tip_BasicLogic_Mid"));
            AddCode(p, L("BasicLogic_Trim")        + "  —  " + L("Tip_BasicLogic_Trim"));
            AddCode(p, L("BasicLogic_Upper")       + "  —  " + L("Tip_BasicLogic_Upper"));
            AddCode(p, L("BasicLogic_Lower")       + "  —  " + L("Tip_BasicLogic_Lower"));
            AddCode(p, L("BasicLogic_Replace")     + "  —  " + L("Tip_BasicLogic_Replace"));
            AddCode(p, L("BasicLogic_Len")         + "  —  " + L("Tip_BasicLogic_Len"));
            AddCode(p, L("BasicLogic_Contains")    + "  —  " + L("Tip_BasicLogic_Contains"));
            AddCode(p, L("BasicLogic_StartsWith")  + "  —  " + L("Tip_BasicLogic_StartsWith"));
            AddCode(p, L("BasicLogic_EndsWith")    + "  —  " + L("Tip_BasicLogic_EndsWith"));
            AddCode(p, L("BasicLogic_IsEmpty")     + "  —  " + L("Tip_BasicLogic_IsEmpty"));
            AddCode(p, L("BasicLogic_Default")     + "  —  " + L("Tip_BasicLogic_Default"));
            AddCode(p, L("BasicLogic_Abs")         + "  —  " + L("Tip_BasicLogic_Abs"));

            AddRule(p);

            // Global actions
            AddL2Header(p, L("Info_Cards_GlobalActionsHeader"));
            AddCode(p, L("Info_Cards_GlobalActions_Desc"));

            return p;
        }

        // ─── helpers ──────────────────────────────────────────────────────

        static string L(string key) => LanguageLoader.Get(key);

        static StackPanel NewPanel() =>
            new StackPanel { Margin = new Thickness(0, 0, 8, 0) };

        static void AddL1Header(StackPanel p, string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "CheckupPrimaryText");
            p.Children.Add(tb);
        }

        static void AddL2Header(StackPanel p, string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 2)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "CheckupPrimaryText");
            p.Children.Add(tb);
        }

        static void AddL3Header(StackPanel p, string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontStyle = FontStyles.Italic,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 1)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "CheckupLabelText");
            p.Children.Add(tb);
        }

        static void AddPara(StackPanel p, string text, double leftMargin = 0)
        {
            if (string.IsNullOrEmpty(text)) return;
            var tb = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(leftMargin, 2, 0, 0)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "CheckupPrimaryText");
            p.Children.Add(tb);
        }

        static void AddBullet(StackPanel p, string text)
        {
            var tb = new TextBlock
            {
                Text = "• " + text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "CheckupPrimaryText");
            p.Children.Add(tb);
        }

        static void AddCode(StackPanel p, string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 11,
                Margin = new Thickness(12, 1, 0, 1)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "CheckupSecondaryText");
            p.Children.Add(tb);
        }

        static void AddRule(StackPanel p)
        {
            var b = new Border
            {
                Height = 1,
                Margin = new Thickness(0, 10, 0, 8)
            };
            b.SetResourceReference(Border.BackgroundProperty, "CheckupSeparator");
            p.Children.Add(b);
        }

        static void AddRoleEntry(StackPanel p, string roleName, string description)
        {
            var nameBlock = new TextBlock
            {
                Text = roleName,
                FontFamily = new FontFamily("Consolas, Courier New"),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 6, 0, 0)
            };
            nameBlock.SetResourceReference(TextBlock.ForegroundProperty, "CheckupPrimaryText");
            p.Children.Add(nameBlock);
            AddPara(p, description, 12);
        }

        static void AddCardSection(StackPanel p, string cardName, string description)
        {
            var tb = new TextBlock
            {
                Text = cardName,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 1)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "CheckupLabelText");
            p.Children.Add(tb);
            AddPara(p, description, 12);
        }
    }
}
