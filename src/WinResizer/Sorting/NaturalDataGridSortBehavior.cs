using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace WinResizer.Sorting;

public static class NaturalDataGridSortBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(NaturalDataGridSortBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty UseNaturalStringProperty =
        DependencyProperty.RegisterAttached(
            "UseNaturalString",
            typeof(bool),
            typeof(NaturalDataGridSortBehavior),
            new PropertyMetadata(false));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetUseNaturalString(DependencyObject element, bool value) => element.SetValue(UseNaturalStringProperty, value);

    public static bool GetUseNaturalString(DependencyObject element) => (bool)element.GetValue(UseNaturalStringProperty);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not DataGrid grid)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            grid.Sorting += Grid_Sorting;
        }
        else
        {
            grid.Sorting -= Grid_Sorting;
        }
    }

    private static void Grid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (sender is not DataGrid grid || e.Column == null)
        {
            return;
        }

        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        if (view == null)
        {
            return;
        }

        if (GetUseNaturalString(e.Column))
        {
            if (view is not ListCollectionView listView ||
                !TryCreateStringAccessor(grid.ItemsSource, e.Column.SortMemberPath, out var accessor))
            {
                return;
            }

            e.Handled = true;
            var direction = e.Column.SortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;

            ClearSortState(grid, listView);
            listView.CustomSort = new NaturalPropertyComparer(accessor, direction == ListSortDirection.Descending);
            e.Column.SortDirection = direction;
            return;
        }

        if (view is ListCollectionView typedView && typedView.CustomSort != null)
        {
            typedView.CustomSort = null;
            ClearColumnDirections(grid);
        }
    }

    private static void ClearSortState(DataGrid grid, ListCollectionView view)
    {
        view.CustomSort = null;
        view.SortDescriptions.Clear();
        ClearColumnDirections(grid);
    }

    private static void ClearColumnDirections(DataGrid grid)
    {
        foreach (var column in grid.Columns)
        {
            column.SortDirection = null;
        }
    }

    private static bool TryCreateStringAccessor(object? source, string? sortMemberPath, out Func<object, string?> accessor)
    {
        accessor = null!;
        if (source == null || string.IsNullOrWhiteSpace(sortMemberPath))
        {
            return false;
        }

        var itemType = GetItemType(source);
        if (itemType == null)
        {
            return false;
        }

        var property = TypeDescriptor.GetProperties(itemType)[sortMemberPath];
        if (property == null || property.PropertyType != typeof(string))
        {
            return false;
        }

        accessor = item => item == null ? null : property.GetValue(item) as string;
        return true;
    }

    private static Type? GetItemType(object source)
    {
        var sourceType = source.GetType();
        if (sourceType.IsGenericType && sourceType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return sourceType.GetGenericArguments()[0];
        }

        return sourceType.GetInterfaces()
            .Where(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(type => type.GetGenericArguments()[0])
            .FirstOrDefault();
    }

    private sealed class NaturalPropertyComparer : IComparer
    {
        private readonly Func<object, string?> _accessor;
        private readonly bool _descending;

        public NaturalPropertyComparer(Func<object, string?> accessor, bool descending)
        {
            _accessor = accessor;
            _descending = descending;
        }

        public int Compare(object? x, object? y)
        {
            var result = NaturalStringComparer.Instance.Compare(_accessor(x!), _accessor(y!));
            return _descending ? -result : result;
        }
    }
}
