using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorTextBlock.Avalonia;
using TrackExtractor.Services;

namespace TrackExtractor.Views;

public partial class MediaGuideWindow : Window
{
    private static LocalizationManager L => LocalizationManager.Active;
    private readonly Dictionary<MediaGuideArticle, TreeViewItem> _articleItems = [];
    private MediaGuideSearch? _search;
    private MediaGuideDiagramCatalog? _diagrams;
    private MediaGuideArticle? _currentArticle;
    private IReadOnlyList<MediaGuideMatch> _currentMatches = [];
    private CTextBlock? _highlightedBlock;
    private TextBlock? _highlightedDiagramLabel;
    private int _currentMatchIndex = -1;
    private int _navigationVersion;
    private int _articleVersion;
    private string? _loadedLanguage;

    public MediaGuideWindow()
    {
        InitializeComponent();
        L.PropertyChanged += Localization_PropertyChanged;
        Closed += (_, _) => L.PropertyChanged -= Localization_PropertyChanged;
        try
        {
            LoadGuide(L.Language);
        }
        catch (Exception ex)
        {
            ArticleViewer.Markdown = L.Format("GuideLoadFailed", ex.Message);
            SearchBox.IsEnabled = false;
        }
        SizeForScreen();
    }

    private void Localization_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Item[]") return;
        var language = L.Language;
        if (_loadedLanguage != language)
        {
            try { LoadGuide(language); }
            catch (Exception ex) { ArticleViewer.Markdown = L.Format("GuideLoadFailed", ex.Message); }
        }
        GuideFallbackNotice.Text = L.Get("GuideRussianOriginalNotice");
        NavigationTitle.Text = string.IsNullOrWhiteSpace(SearchBox.Text)
            ? L.Get("GuideContents") : L.Get("GuideSearchResults");
        if (_currentMatchIndex < 0 && !string.IsNullOrWhiteSpace(SearchBox.Text))
            SearchPosition.Text = ResultsList.Items.Count == 0 ? "0"
                : L.Format("GuideArticleCount", ResultsList.Items.Count);
    }

    private void LoadGuide(string language)
    {
        var guideLanguage = LanguagePackCatalog.GuideLanguageFor(language);
        var guide = MediaGuideContent.Load(guideLanguage);
        var diagrams = MediaGuideDiagramCatalog.Load(guideLanguage);
        _search = null;
        _articleVersion++;
        ClearHighlight();
        _currentArticle = null;
        _currentMatches = [];
        _currentMatchIndex = -1;
        SearchBox.Text = "";
        ResultsList.Items.Clear();
        ClearSearchButton.IsEnabled = false;
        ContentsTree.IsVisible = true;
        ResultsList.IsVisible = false;
        NoResultsText.IsVisible = false;
        SearchPosition.Text = "";
        UpdateNavigationButtons();
        ContentsTree.Items.Clear();
        _articleItems.Clear();
        _diagrams = diagrams;
        TreeViewItem? firstArticle = null;
        foreach (var section in guide.Sections)
        {
            var sectionItem = new TreeViewItem { Header = section.Title, IsExpanded = true };
            foreach (var article in section.Articles)
            {
                var articleItem = new TreeViewItem { Header = article.Title, Tag = article };
                sectionItem.Items.Add(articleItem);
                _articleItems.Add(article, articleItem);
                firstArticle ??= articleItem;
            }
            ContentsTree.Items.Add(sectionItem);
        }
        _search = new MediaGuideSearch(guide, diagrams);
        _loadedLanguage = language;
        GuideFallbackNotice.Text = L.Get("GuideRussianOriginalNotice");
        GuideFallbackNotice.IsVisible = guideLanguage != language;
        if (firstArticle is not null)
        {
            firstArticle.IsSelected = true;
            ShowArticle((MediaGuideArticle)firstArticle.Tag!);
        }
    }

    private void Contents_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ArticleViewer is null || ContentsTree?.SelectedItem is not TreeViewItem { Tag: MediaGuideArticle article })
            return;
        ShowArticle(article);
        ClearCurrentMatch();
    }

    private void Search_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_search is null || SearchBox is null || ResultsList is null) return;
        _navigationVersion++;
        ClearHighlight();
        _currentMatches = [];
        _currentMatchIndex = -1;
        var query = SearchBox.Text?.Trim();
        var hasQuery = !string.IsNullOrEmpty(query);
        ClearSearchButton.IsEnabled = hasQuery;
        ContentsTree.IsVisible = !hasQuery;
        ResultsList.IsVisible = hasQuery;
        NavigationTitle.Text = L.Get(hasQuery ? "GuideSearchResults" : "GuideContents");
        ResultsList.Items.Clear();
        if (!hasQuery)
        {
            NoResultsText.IsVisible = false;
            SearchPosition.Text = "";
            UpdateNavigationButtons();
            return;
        }

        var results = _search.Find(query);
        foreach (var result in results)
        {
            var item = new ListBoxItem { Tag = result };
            var text = new StackPanel { Spacing = 3, Margin = new Thickness(5, 7) };
            text.Children.Add(new TextBlock
            {
                Text = result.Article.Title, FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap
            });
            text.Children.Add(new TextBlock
            {
                Text = result.First.Snippet, FontSize = 12, Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap
            });
            item.Content = text;
            ResultsList.Items.Add(item);
        }
        NoResultsText.IsVisible = results.Count == 0;
        SearchPosition.Text = results.Count == 0 ? "0" : L.Format("GuideArticleCount", results.Count);
        UpdateNavigationButtons();
        if (results.Count > 0) ResultsList.SelectedIndex = 0;
    }

    private void Results_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ResultsList?.SelectedItem is not ListBoxItem { Tag: MediaGuideSearchResult result }) return;
        var item = _articleItems[result.Article];
        var section = ContentsTree.Items.OfType<TreeViewItem>().First(parent => parent.Items.Contains(item));
        section.IsExpanded = true;
        ContentsTree.SelectedItem = item;
        item.IsSelected = true;
        ShowArticle(result.Article);
        _currentMatches = result.Matches;
        NavigateToMatch(0);
    }

    private void ClearSearch_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    private void PreviousMatch_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_currentMatches.Count > 0)
            NavigateToMatch((_currentMatchIndex + _currentMatches.Count - 1) % _currentMatches.Count);
    }

    private void NextMatch_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_currentMatches.Count > 0)
            NavigateToMatch((_currentMatchIndex + 1) % _currentMatches.Count);
    }

    private void ShowArticle(MediaGuideArticle article)
    {
        if (ReferenceEquals(_currentArticle, article)) return;
        _navigationVersion++;
        ClearHighlight();
        _currentArticle = article;
        ArticleViewer.Markdown = article.Markdown;
        ArticleViewer.ScrollValue = default;
        var version = ++_articleVersion;
        Dispatcher.UIThread.Post(() => InsertDiagrams(version, 0), DispatcherPriority.Loaded);
    }

    private void InsertDiagrams(int version, int attempt)
    {
        if (version != _articleVersion || _currentArticle is null || _diagrams is null) return;
        ArticleViewer.UpdateLayout();
        var markers = ArticleViewer.GetVisualDescendants().OfType<CTextBlock>()
            .Select(block => (Block: block, Id: MediaGuideDiagramCatalog.MarkerId(block.Text)))
            .Where(entry => entry.Id is not null).ToArray();
        if (markers.Length == 0 && _currentArticle.Markdown.Contains("{{diagram:", StringComparison.Ordinal) && attempt < 3)
        {
            Dispatcher.UIThread.Post(() => InsertDiagrams(version, attempt + 1), DispatcherPriority.Render);
            return;
        }
        foreach (var (block, id) in markers)
        {
            var diagram = MediaGuideDiagramView.Create(_diagrams.Get(id!));
            switch (block.GetVisualParent())
            {
                case Panel panel:
                    var position = panel.Children.IndexOf(block);
                    if (position >= 0)
                    {
                        panel.Children.RemoveAt(position);
                        panel.Children.Insert(position, diagram);
                    }
                    break;
                case Decorator decorator when ReferenceEquals(decorator.Child, block):
                    decorator.Child = diagram;
                    break;
                case ContentControl content when ReferenceEquals(content.Content, block):
                    content.Content = diagram;
                    break;
            }
        }
    }

    private void NavigateToMatch(int index)
    {
        if (index < 0 || index >= _currentMatches.Count) return;
        _currentMatchIndex = index;
        SearchPosition.Text = $"{index + 1} / {_currentMatches.Count}";
        UpdateNavigationButtons();
        var match = _currentMatches[index];
        var version = ++_navigationVersion;
        Dispatcher.UIThread.Post(() => HighlightMatch(match, version, 0), DispatcherPriority.Loaded);
    }

    private void HighlightMatch(MediaGuideMatch match, int version, int attempt)
    {
        if (version != _navigationVersion || !ReferenceEquals(_currentArticle, match.Article) || _search is null)
            return;
        ArticleViewer.UpdateLayout();
        if (match.DiagramId is not null)
        {
            var label = ArticleViewer.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text =>
                text.Tag is MediaGuideDiagramLabel key && key.DiagramId == match.DiagramId && key.NodeId == match.NodeId);
            if (label is null)
            {
                if (attempt < 3)
                    Dispatcher.UIThread.Post(() => HighlightMatch(match, version, attempt + 1), DispatcherPriority.Render);
                else
                    SearchPosition.Text = L.Get("GuideNavigationUnavailable");
                return;
            }
            ClearHighlight();
            label.Classes.Add("guide-hit");
            label.BringIntoView();
            _highlightedDiagramLabel = label;
            return;
        }
        var rendered = ArticleViewer.GetVisualDescendants().OfType<CTextBlock>().ToArray();
        var indexed = _search.BlocksFor(match.Article);
        if (rendered.Length != indexed.Count || rendered.Where((block, i) => block.Text != indexed[i]).Any())
        {
            SearchPosition.Text = L.Get("GuideNavigationUnavailable");
            return;
        }
        var target = rendered[match.BlockIndex];
        if (target.Bounds.Height <= 0)
        {
            if (attempt < 2)
                Dispatcher.UIThread.Post(() => HighlightMatch(match, version, attempt + 1), DispatcherPriority.Render);
            else
                SearchPosition.Text = L.Get("GuideNavigationUnavailable");
            return;
        }
        ClearHighlight();
        try
        {
            target.Select(match.Start, match.Start + match.Length);
            if (!string.Equals(target.GetSelectedText(), match.Text, StringComparison.OrdinalIgnoreCase))
            {
                target.ClearSelection();
                SearchPosition.Text = L.Get("GuideNavigationUnavailable");
                return;
            }
            var rect = FindMatchRect(target, match.Start);
            target.Select(match.Start, match.Start + match.Length);
            target.BringIntoView(rect);
            _highlightedBlock = target;
        }
        catch (Exception)
        {
            target.ClearSelection();
            SearchPosition.Text = L.Get("GuideNavigationUnavailable");
        }
    }

    private static Rect FindMatchRect(CTextBlock block, int start)
    {
        var lastY = Math.Max(0, (int)Math.Ceiling(block.Bounds.Height) - 1);
        if (PrefixAt(block, block.Bounds.Width, lastY) <= start)
            throw new InvalidOperationException("Не удалось определить строку совпадения.");
        var low = 0;
        var high = lastY;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (PrefixAt(block, block.Bounds.Width, mid) > start) high = mid;
            else low = mid + 1;
        }
        var y = low;
        var lastX = Math.Max(0, (int)Math.Ceiling(block.Bounds.Width));
        if (PrefixAt(block, lastX, y) <= start)
            throw new InvalidOperationException("Не удалось определить положение совпадения в строке.");
        low = 0;
        high = lastX;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (PrefixAt(block, mid, y) > start) high = mid;
            else low = mid + 1;
        }
        var x = low;
        return new Rect(Math.Max(0, x - 8), Math.Max(0, y - 2), 32, Math.Max(18, block.FontSize * 1.5));
    }

    private static int PrefixAt(CTextBlock block, double x, double y)
    {
        block.Select(block.GetBegin(), block.CalcuatePointerFrom(x, y));
        return block.GetSelectedText().Length;
    }

    private void ClearCurrentMatch()
    {
        _navigationVersion++;
        ClearHighlight();
        _currentMatches = [];
        _currentMatchIndex = -1;
        UpdateNavigationButtons();
    }

    private void ClearHighlight()
    {
        _highlightedBlock?.ClearSelection();
        _highlightedBlock = null;
        _highlightedDiagramLabel?.Classes.Remove("guide-hit");
        _highlightedDiagramLabel = null;
    }

    private void UpdateNavigationButtons()
    {
        var enabled = _currentMatches.Count > 1;
        PreviousMatchButton.IsEnabled = enabled;
        NextMatchButton.IsEnabled = enabled;
    }

    private void SizeForScreen()
    {
        var screen = Screens.Primary;
        if (screen is null) return;
        var scale = screen.Scaling;
        Width = Math.Clamp(screen.WorkingArea.Width / scale * 0.65, MinWidth, 1450);
        Height = Math.Clamp(screen.WorkingArea.Height / scale * 0.80, MinHeight, 950);
    }
}
