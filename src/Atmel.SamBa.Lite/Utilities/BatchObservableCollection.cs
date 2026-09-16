using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;


namespace Anp.Atmel.SamBa.Lite.Utilities
{
    /// <summary>
    /// An <see cref="ObservableCollection{T}"/> that supports batch add and remove
    /// operations with a single <see cref="NotifyCollectionChangedAction.Reset"/>
    /// notification instead of per-item events.
    /// </summary>
    /// <remarks>
    /// WPF's <c>ListBox</c> throws on <see cref="NotifyCollectionChangedAction.Add"/>
    /// with multiple items, so standard range-add helpers don't work when the collection
    /// is bound to a UI control. This class bypasses that by writing directly to the
    /// underlying <see cref="Collection{T}.Items"/> list (which has no notifications)
    /// and raising a single <c>Reset</c> afterwards.
    /// </remarks>
    public class BatchObservableCollection<T> : ObservableCollection<T>
    {
        private bool _suppressNotifications;

        /// <summary>
        /// Adds multiple items and raises a single <see cref="NotifyCollectionChangedAction.Reset"/>.
        /// </summary>
        public void AddBatch(IList<T> items)
        {
            if (items == null || items.Count == 0)
                return;

            _suppressNotifications = true;
            try
            {
                foreach (var item in items)
                    Items.Add(item);
            }
            finally
            {
                _suppressNotifications = false;
            }

            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        /// <summary>
        /// Removes <paramref name="count"/> items from the beginning of the collection
        /// and raises a single <see cref="NotifyCollectionChangedAction.Reset"/>.
        /// </summary>
        public void RemoveLeading(int count)
        {
            if (count <= 0)
                return;

            if (count >= Count)
            {
                Items.Clear();
            }
            else
            {
                // Copy the tail, clear, re-add. O(n) vs O(n²) from repeated RemoveAt(0).
                var keep = new T[Count - count];
                for (int i = 0; i < keep.Length; i++)
                    keep[i] = Items[count + i];

                _suppressNotifications = true;
                try
                {
                    Items.Clear();
                    for (int i = 0; i < keep.Length; i++)
                        Items.Add(keep[i]);
                }
                finally
                {
                    _suppressNotifications = false;
                }
            }

            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        /// <inheritdoc />
        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            if (!_suppressNotifications)
                base.OnCollectionChanged(e);
        }
    }
}
