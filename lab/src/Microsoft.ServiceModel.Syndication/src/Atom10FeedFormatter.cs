// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.ServiceModel.Syndication
{
    using Microsoft.ServiceModel.Syndication.Resources;
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Globalization;
    using System.Runtime.CompilerServices;
    using System.ServiceModel.Channels;
    using System.Threading.Tasks;
    using System.Xml;
    using System.Xml.Serialization;

    [XmlRoot(ElementName = Atom10Constants.FeedTag, Namespace = Atom10Constants.Atom10Namespace)]
    public class Atom10FeedFormatter : SyndicationFeedFormatter
    {
        internal static readonly TimeSpan zeroOffset = new TimeSpan(0, 0, 0);
        internal const string XmlNs = "http://www.w3.org/XML/1998/namespace";
        internal const string XmlNsNs = "http://www.w3.org/2000/xmlns/";
        private static readonly XmlQualifiedName s_atom10Href = new XmlQualifiedName(Atom10Constants.HrefTag, string.Empty);
        private static readonly XmlQualifiedName s_atom10Label = new XmlQualifiedName(Atom10Constants.LabelTag, string.Empty);
        private static readonly XmlQualifiedName s_atom10Length = new XmlQualifiedName(Atom10Constants.LengthTag, string.Empty);
        private static readonly XmlQualifiedName s_atom10Relative = new XmlQualifiedName(Atom10Constants.RelativeTag, string.Empty);
        private static readonly XmlQualifiedName s_atom10Scheme = new XmlQualifiedName(Atom10Constants.SchemeTag, string.Empty);
        private static readonly XmlQualifiedName s_atom10Term = new XmlQualifiedName(Atom10Constants.TermTag, string.Empty);
        private static readonly XmlQualifiedName s_atom10Title = new XmlQualifiedName(Atom10Constants.TitleTag, string.Empty);
        private static readonly XmlQualifiedName s_atom10Type = new XmlQualifiedName(Atom10Constants.TypeTag, string.Empty);
        private static readonly UriGenerator s_idGenerator = new UriGenerator();
        private const string Rfc3339LocalDateTimeFormat = "yyyy-MM-ddTHH:mm:sszzz";
        private const string Rfc3339UTCDateTimeFormat = "yyyy-MM-ddTHH:mm:ssZ";
        private Type _feedType;
        private int _maxExtensionSize;
        private bool _preserveAttributeExtensions;
        private bool _preserveElementExtensions;

        public Atom10FeedFormatter()
            : this(typeof(SyndicationFeed))
        {
        }

        public Atom10FeedFormatter(Type feedTypeToCreate)
            : base()
        {
            if (feedTypeToCreate == null)
            {
                throw new ArgumentException("feedTypeToCreate");
            }
            if (!typeof(SyndicationFeed).IsAssignableFrom(feedTypeToCreate))
            {
                throw new ArgumentException(String.Format(SR.InvalidObjectTypePassed, "feedTypeToCreate", "SyndicationFeed"));
            }
            _maxExtensionSize = int.MaxValue;
            _preserveAttributeExtensions = _preserveElementExtensions = true;
            _feedType = feedTypeToCreate;
        }

        public Atom10FeedFormatter(SyndicationFeed feedToWrite)
            : base(feedToWrite)
        {
            // No need to check that the parameter passed is valid - it is checked by the c'tor of the base class
            _maxExtensionSize = int.MaxValue;
            _preserveAttributeExtensions = _preserveElementExtensions = true;
            _feedType = feedToWrite.GetType();
        }

        public bool PreserveAttributeExtensions
        {
            get { return _preserveAttributeExtensions; }
            set { _preserveAttributeExtensions = value; }
        }

        public bool PreserveElementExtensions
        {
            get { return _preserveElementExtensions; }
            set { _preserveElementExtensions = value; }
        }

        public override string Version
        {
            get { return SyndicationVersions.Atom10; }
        }

        protected Type FeedType
        {
            get
            {
                return _feedType;
            }
        }

        public override bool CanRead(XmlReader reader)
        {
            if (reader == null)
            {
                throw new ArgumentNullException("reader");
            }

            return reader.IsStartElement(Atom10Constants.FeedTag, Atom10Constants.Atom10Namespace);
        }
        
        public override async Task ReadFromAsync(XmlReader reader)
        {
            if (!CanRead(reader))
            {
                throw new XmlException(String.Format(SR.UnknownFeedXml, reader.LocalName, reader.NamespaceURI));
            }

            SetFeed(CreateFeedInstance());
            await ReadFeedFromAsync(XmlReaderWrapper.CreateFromReader(reader), this.Feed, false);
        }

        public override void WriteTo(XmlWriter writer)
        {
            if (writer == null)
            {
                throw new ArgumentNullException("writer");
            }
            writer.WriteStartElement(Atom10Constants.FeedTag, Atom10Constants.Atom10Namespace);
            WriteFeed(writer);
            writer.WriteEndElement();
        }

        internal static async Task ReadCategoryAsync(XmlReaderWrapper reader, SyndicationCategory category, string version, bool preserveAttributeExtensions, bool preserveElementExtensions, int _maxExtensionSize)
        {
            await MoveToStartElementAsync(reader);
            bool isEmpty = reader.IsEmptyElement;
            if (reader.HasAttributes)
            {
                while (reader.MoveToNextAttribute())
                {
                    string value = await reader.GetValueAsync();
                    bool notHandled = false;

                    if(reader.NamespaceURI == string.Empty)
                    {
                        switch (reader.LocalName)
                        {
                            case Atom10Constants.TermTag:
                                category.Name = value;
                                break;

                            case Atom10Constants.SchemeTag:
                                category.Scheme = value;
                                break;

                            case Atom10Constants.LabelTag:
                                category.Label = value;
                                break;

                            default:
                                notHandled = true;
                                break;
                        }
                    }
                    else
                    {
                        notHandled = true;
                    }

                    if (notHandled)
                    {
                        string ns = reader.NamespaceURI;
                        string name = reader.LocalName;
                        if (FeedUtils.IsXmlns(name, ns))
                        {
                            continue;
                        }

                        if (!TryParseAttribute(name, ns, value, category, version))
                        {
                            if (preserveAttributeExtensions)
                            {
                                category.AttributeExtensions.Add(new XmlQualifiedName(name, ns), value);
                            }
                        }
                    }
                }
            }

            if (!isEmpty)
            {
                await reader.ReadStartElementAsync();
                XmlBuffer buffer = null;
                XmlDictionaryWriter extWriter = null;
                try
                {
                    while (await reader.IsStartElementAsync())
                    {
                        if (TryParseElement(reader, category, version))
                        {
                            continue;
                        }
                        else if (!preserveElementExtensions)
                        {
                            await reader.SkipAsync();
                        }
                        else
                        {
                            if (buffer == null)
                            {
                                buffer = new XmlBuffer(_maxExtensionSize);
                                extWriter = buffer.OpenSection(XmlDictionaryReaderQuotas.Max);
                                extWriter.WriteStartElement(Rss20Constants.ExtensionWrapperTag);
                            }

                            await XmlReaderWrapper.WriteNodeAsync(extWriter, reader, false);
                        }
                    }

                    LoadElementExtensions(buffer, extWriter, category);
                }
                finally
                {
                    if (extWriter != null)
                    {
                        ((IDisposable)extWriter).Dispose();
                    }
                }

                await reader.ReadEndElementAsync();
            }
            else
            {
                await reader.ReadStartElementAsync();
            }
        }

        internal static async Task<TextSyndicationContent> ReadTextContentFromAsync(XmlReaderWrapper reader, string context, bool preserveAttributeExtensions)
        {
            string type = reader.GetAttribute(Atom10Constants.TypeTag);
            return await ReadTextContentFromHelper(reader, type, context, preserveAttributeExtensions);
        }

        internal static  void WriteCategory(XmlWriter writer, SyndicationCategory category, string version)
        {
            writer.WriteStartElement(Atom10Constants.CategoryTag, Atom10Constants.Atom10Namespace);
            WriteAttributeExtensions(writer, category, version);
            string categoryName = category.Name ?? string.Empty;
            if (!category.AttributeExtensions.ContainsKey(s_atom10Term))
            {
                writer.WriteAttributeString(Atom10Constants.TermTag, categoryName);
            }
            if (!string.IsNullOrEmpty(category.Label) && !category.AttributeExtensions.ContainsKey(s_atom10Label))
            {
                writer.WriteAttributeString(Atom10Constants.LabelTag, category.Label);
            }
            if (!string.IsNullOrEmpty(category.Scheme) && !category.AttributeExtensions.ContainsKey(s_atom10Scheme))
            {
                writer.WriteAttributeString(Atom10Constants.SchemeTag, category.Scheme);
            }
            WriteElementExtensions(writer, category, version);
            writer.WriteEndElement();
        }

        internal async Task ReadItemFrom(XmlReaderWrapper reader, SyndicationItem result)
        {
            await ReadItemFromAsync(reader, result, null);
        }

        internal async Task<bool> TryParseFeedElementFromAsync(XmlReaderWrapper reader, SyndicationFeed result)
        {

            string name = reader.LocalName;
            string ns = reader.NamespaceURI;

            if(ns == Atom10Constants.Atom10Namespace)
            {
                switch (name)
                {
                    case Atom10Constants.AuthorTag:
                        result.Authors.Add(await ReadPersonFromAsync(reader, result));
                        break;
                    case Atom10Constants.CategoryTag:
                        result.Categories.Add(await ReadCategoryFromAsync(reader, result));
                        break;
                    case Atom10Constants.ContributorTag:
                        result.Contributors.Add(await ReadPersonFromAsync(reader, result));
                        break;
                    case Atom10Constants.GeneratorTag:
                        result.Generator = await reader.ReadElementStringAsync();
                        break;
                    case Atom10Constants.IdTag:
                        result.Id = await reader.ReadElementStringAsync();
                        break;
                    case Atom10Constants.LinkTag:
                        result.Links.Add(await ReadLinkFromAsync(reader, result));
                        break;
                    case Atom10Constants.LogoTag:
                        result.ImageUrl = new Uri(await reader.ReadElementStringAsync(), UriKind.RelativeOrAbsolute);
                        break;
                    case Atom10Constants.RightsTag:
                        result.Copyright = await ReadTextContentFromAsync(reader, "//atom:feed/atom:rights[@type]");
                        break;
                    case Atom10Constants.SubtitleTag:
                        result.Description = await ReadTextContentFromAsync(reader, "//atom:feed/atom:subtitle[@type]");
                        break;
                    case Atom10Constants.TitleTag:
                        result.Title = await ReadTextContentFromAsync(reader, "//atom:feed/atom:title[@type]");
                        break;
                    case Atom10Constants.UpdatedTag:
                        await reader.ReadStartElementAsync();
                        result.LastUpdatedTime = DateFromString(await reader.ReadStringAsync(), reader);
                        await reader.ReadEndElementAsync();
                        break;
                    default:
                        return false;
                }
                return true;
            }
            return false;

            // original
            //if (await reader.IsStartElementAsync(Atom10Constants.AuthorTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.Authors.Add(await ReadPersonFromAsync(reader, result));
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.CategoryTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.Categories.Add(await ReadCategoryFromAsync(reader, result));
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.ContributorTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.Contributors.Add(await ReadPersonFromAsync(reader, result));
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.GeneratorTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.Generator = await reader.ReadElementStringAsync();
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.IdTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.Id = await reader.ReadElementStringAsync();
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.LinkTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.Links.Add(await ReadLinkFromAsync(reader, result));
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.LogoTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.ImageUrl = new Uri(await reader.ReadElementStringAsync(), UriKind.RelativeOrAbsolute);
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.RightsTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.Copyright = await ReadTextContentFromAsync(reader, "//atom:feed/atom:rights[@type]");
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.SubtitleTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.Description = await ReadTextContentFromAsync(reader, "//atom:feed/atom:subtitle[@type]");
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.TitleTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.Title = await ReadTextContentFromAsync(reader, "//atom:feed/atom:title[@type]");
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.UpdatedTag, Atom10Constants.Atom10Namespace))
            //{
            //    await reader.ReadStartElementAsync();
            //    result.LastUpdatedTime = DateFromString(await reader.ReadStringAsync(), reader);
            //    await reader.ReadEndElementAsync();
            //}
            //else
            //{
            //    return false;
            //}
            //return true;
        }

        internal async Task<bool> TryParseItemElementFromAsync(XmlReaderWrapper reader, SyndicationItem result)
        {

            string name = reader.LocalName;
            string ns = reader.NamespaceURI;

            if(ns == Atom10Constants.Atom10Namespace)
            {
                switch (name)
                {
                    case Atom10Constants.AuthorTag:
                        result.Authors.Add(await ReadPersonFromAsync(reader, result));
                        break;
                    case Atom10Constants.CategoryTag:
                        result.Categories.Add(await ReadCategoryFromAsync(reader, result));
                        break;
                    case Atom10Constants.ContentTag:
                        result.Content = await ReadContentFromAsync(reader, result);
                        break;
                    case Atom10Constants.ContributorTag:
                        result.Contributors.Add(await ReadPersonFromAsync(reader, result));
                        break;
                    case Atom10Constants.IdTag:
                        result.Id = await reader.ReadElementStringAsync();
                        break;
                    case Atom10Constants.LinkTag:
                        result.Links.Add(await ReadLinkFromAsync(reader, result));
                        break;
                    case Atom10Constants.PublishedTag:
                        await reader.ReadStartElementAsync();
                        result.PublishDate = DateFromString(await reader.ReadStringAsync(), reader);
                        await reader.ReadEndElementAsync();
                        break;
                    case Atom10Constants.RightsTag:
                        result.Copyright = await ReadTextContentFromAsync(reader, "//atom:feed/atom:entry/atom:rights[@type]");
                        break;
                    case Atom10Constants.SourceFeedTag:
                        await reader.ReadStartElementAsync();
                        result.SourceFeed = await ReadFeedFromAsync(reader, new SyndicationFeed(), true); //  isSourceFeed 
                        await reader.ReadEndElementAsync();
                        break;
                    case Atom10Constants.SummaryTag:
                        result.Summary = await ReadTextContentFromAsync(reader, "//atom:feed/atom:entry/atom:summary[@type]");
                        break;
                    case Atom10Constants.TitleTag:
                        result.Title = await ReadTextContentFromAsync(reader, "//atom:feed/atom:entry/atom:title[@type]");
                        break;
                    case Atom10Constants.UpdatedTag:
                        await reader.ReadStartElementAsync();
                        result.LastUpdatedTime = DateFromString(await reader.ReadStringAsync(), reader);
                        await reader.ReadEndElementAsync();
                        break;
                    default:
                        return false;
                }
                return true;
            }
            return false;


            //original
            //if (await reader.IsStartElementAsync(Atom10Constants.AuthorTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.Authors.Add(await ReadPersonFromAsync(reader, result));
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.CategoryTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.Categories.Add(await ReadCategoryFromAsync(reader, result));
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.ContentTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.Content = await ReadContentFromAsync(reader, result);
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.ContributorTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.Contributors.Add(await ReadPersonFromAsync(reader, result));
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.IdTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.Id = await reader.ReadElementStringAsync();
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.LinkTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.Links.Add(await ReadLinkFromAsync(reader, result));
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.PublishedTag, Atom10Constants.Atom10Namespace))
            //{
            //    await reader.ReadStartElementAsync();
            //    result.PublishDate = DateFromString(await reader.ReadStringAsync(), reader);
            //    await reader.ReadEndElementAsync();
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.RightsTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.Copyright = await ReadTextContentFromAsync(reader, "//atom:feed/atom:entry/atom:rights[@type]");
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.SourceFeedTag, Atom10Constants.Atom10Namespace))
            //{
            //    await reader.ReadStartElementAsync();
            //    result.SourceFeed = await ReadFeedFromAsync(reader, new SyndicationFeed(), true); //  isSourceFeed 
            //    await reader.ReadEndElementAsync();
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.SummaryTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.Summary = await ReadTextContentFromAsync(reader, "//atom:feed/atom:entry/atom:summary[@type]");
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.TitleTag, Atom10Constants.Atom10Namespace))
            //{
            //    result.Title = await ReadTextContentFromAsync(reader, "//atom:feed/atom:entry/atom:title[@type]");
            //}
            //else if (await reader.IsStartElementAsync(Atom10Constants.UpdatedTag, Atom10Constants.Atom10Namespace))
            //{
            //    await reader.ReadStartElementAsync();
            //    result.LastUpdatedTime = DateFromString(await reader.ReadStringAsync(), reader);
            //    await reader.ReadEndElementAsync();
            //}
            //else
            //{
            //    return false;
            //}
            //return true;
        }

        internal void WriteContentTo(XmlWriter writer, string elementName, SyndicationContent content)
        {
            if (content != null)
            {
                content.WriteTo(writer, elementName, Atom10Constants.Atom10Namespace);
            }
        }

        internal void WriteElement(XmlWriter writer, string elementName, string value)
        {
            if (value != null)
            {
                writer.WriteElementString(elementName, Atom10Constants.Atom10Namespace, value);
            }
        }

        internal void WriteFeedAuthorsTo(XmlWriter writer, Collection<SyndicationPerson> authors)
        {
            for (int i = 0; i < authors.Count; ++i)
            {
                SyndicationPerson p = authors[i];
                WritePersonTo(writer, p, Atom10Constants.AuthorTag);
            }
        }

        internal void WriteFeedContributorsTo(XmlWriter writer, Collection<SyndicationPerson> contributors)
        {
            for (int i = 0; i < contributors.Count; ++i)
            {
                SyndicationPerson p = contributors[i];
                WritePersonTo(writer, p, Atom10Constants.ContributorTag);
            }
        }

        internal void WriteFeedLastUpdatedTimeTo(XmlWriter writer, DateTimeOffset lastUpdatedTime, bool isRequired)
        {
            if (lastUpdatedTime == DateTimeOffset.MinValue && isRequired)
            {
                lastUpdatedTime = DateTimeOffset.UtcNow;
            }
            if (lastUpdatedTime != DateTimeOffset.MinValue)
            {
                WriteElement(writer, Atom10Constants.UpdatedTag, AsString(lastUpdatedTime));
            }
        }

        internal void WriteItemAuthorsTo(XmlWriter writer, Collection<SyndicationPerson> authors)
        {
            for (int i = 0; i < authors.Count; ++i)
            {
                SyndicationPerson p = authors[i];
                WritePersonTo(writer, p, Atom10Constants.AuthorTag);
            }
        }

        internal void WriteItemContents(XmlWriter dictWriter, SyndicationItem item)
        {
            WriteItemContents(dictWriter, item, null);
        }

        internal void WriteItemContributorsTo(XmlWriter writer, Collection<SyndicationPerson> contributors)
        {
            for (int i = 0; i < contributors.Count; ++i)
            {
                SyndicationPerson p = contributors[i];
                WritePersonTo(writer, p, Atom10Constants.ContributorTag);
            }
        }

        internal void WriteItemLastUpdatedTimeTo(XmlWriter writer, DateTimeOffset lastUpdatedTime)
        {
            if (lastUpdatedTime == DateTimeOffset.MinValue)
            {
                lastUpdatedTime = DateTimeOffset.UtcNow;
            }
            writer.WriteElementString(Atom10Constants.UpdatedTag,
                Atom10Constants.Atom10Namespace,
                AsString(lastUpdatedTime));
        }

        internal void WriteLink(XmlWriter writer, SyndicationLink link, Uri baseUri)
        {
            writer.WriteStartElement(Atom10Constants.LinkTag, Atom10Constants.Atom10Namespace);
            Uri baseUriToWrite = FeedUtils.GetBaseUriToWrite(baseUri, link.BaseUri);
            if (baseUriToWrite != null)
            {
                writer.WriteAttributeString("xml", "base", XmlNs, FeedUtils.GetUriString(baseUriToWrite));
            }

            link.WriteAttributeExtensions(writer, SyndicationVersions.Atom10);
            if (!string.IsNullOrEmpty(link.RelationshipType) && !link.AttributeExtensions.ContainsKey(s_atom10Relative))
            {
                writer.WriteAttributeString(Atom10Constants.RelativeTag, link.RelationshipType);
            }

            if (!string.IsNullOrEmpty(link.MediaType) && !link.AttributeExtensions.ContainsKey(s_atom10Type))
            {
                writer.WriteAttributeString(Atom10Constants.TypeTag, link.MediaType);
            }

            if (!string.IsNullOrEmpty(link.Title) && !link.AttributeExtensions.ContainsKey(s_atom10Title))
            {
                writer.WriteAttributeString(Atom10Constants.TitleTag, link.Title);
            }

            if (link.Length != 0 && !link.AttributeExtensions.ContainsKey(s_atom10Length))
            {
                writer.WriteAttributeString(Atom10Constants.LengthTag, Convert.ToString(link.Length, CultureInfo.InvariantCulture));
            }

            if (!link.AttributeExtensions.ContainsKey(s_atom10Href))
            {
                writer.WriteAttributeString(Atom10Constants.HrefTag, FeedUtils.GetUriString(link.Uri));
            }

            link.WriteElementExtensions(writer, SyndicationVersions.Atom10);
            writer.WriteEndElement();
        }

        protected override SyndicationFeed CreateFeedInstance()
        {
            return SyndicationFeedFormatter.CreateFeedInstance(_feedType);
        }

        protected virtual async Task<SyndicationItem> ReadItemAsync(XmlReader reader, SyndicationFeed feed)
        {
            if (feed == null)
            {
                throw new ArgumentNullException("feed");
            }

            if (reader == null)
            {
                throw new ArgumentNullException("reader");
            }

            SyndicationItem item = CreateItem(feed);

            XmlReaderWrapper readerWrapper = XmlReaderWrapper.CreateFromReader(reader);

            await ReadItemFromAsync(readerWrapper, item, feed.BaseUri);
            return item;
        }

        //not referenced anymore
        protected virtual async Task<IEnumerable<SyndicationItem>> ReadItemsAsync(XmlReader reader, SyndicationFeed feed)
        {
            if (feed == null)
            {
                throw new ArgumentNullException("feed");
            }
            if (reader == null)
            {
                throw new ArgumentNullException("reader");
            }
            NullNotAllowedCollection<SyndicationItem> items = new NullNotAllowedCollection<SyndicationItem>();

            XmlReaderWrapper readerWrapper = XmlReaderWrapper.CreateFromReader(reader);

            while (await readerWrapper.IsStartElementAsync(Atom10Constants.EntryTag, Atom10Constants.Atom10Namespace))
            {
                items.Add(await ReadItemAsync(reader, feed));
            }

            return items;
        }

        protected virtual void WriteItem(XmlWriter writer, SyndicationItem item, Uri feedBaseUri)
        {
            writer.WriteStartElement(Atom10Constants.EntryTag, Atom10Constants.Atom10Namespace);
            WriteItemContents(writer, item, feedBaseUri);
            writer.WriteEndElement();
        }

        protected virtual void WriteItems(XmlWriter writer, IEnumerable<SyndicationItem> items, Uri feedBaseUri)
        {
            if (items == null)
            {
                return;
            }
            foreach (SyndicationItem item in items)
            {
                this.WriteItem(writer, item, feedBaseUri);
            }
        }

        private static async Task<TextSyndicationContent> ReadTextContentFromHelper(XmlReaderWrapper reader, string type, string context, bool preserveAttributeExtensions)
        {
            if (string.IsNullOrEmpty(type))
            {
                type = Atom10Constants.PlaintextType;
            }

            TextSyndicationContentKind kind = new TextSyndicationContentKind();
            switch (type)
            {
                case Atom10Constants.PlaintextType:
                    kind = TextSyndicationContentKind.Plaintext;
                    break;
                case Atom10Constants.HtmlType:
                    kind = TextSyndicationContentKind.Html;
                    break;
                case Atom10Constants.XHtmlType:
                    kind = TextSyndicationContentKind.XHtml;
                    break;

                    throw new XmlException(String.Format(SR.Atom10SpecRequiresTextConstruct, context, type));
            }

            Dictionary<XmlQualifiedName, string> attrs = null;
            if (reader.HasAttributes)
            {
                while (reader.MoveToNextAttribute())
                {
                    if (reader.LocalName == Atom10Constants.TypeTag && reader.NamespaceURI == string.Empty)
                    {
                        continue;
                    }
                    string ns = reader.NamespaceURI;
                    string name = reader.LocalName;
                    if (FeedUtils.IsXmlns(name, ns))
                    {
                        continue;
                    }
                    if (preserveAttributeExtensions)
                    {
                        string value = await reader.GetValueAsync();
                        if (attrs == null)
                        {
                            attrs = new Dictionary<XmlQualifiedName, string>();
                        }
                        attrs.Add(new XmlQualifiedName(name, ns), value);
                    }
                }
            }
            reader.MoveToElement();
            string val = (kind == TextSyndicationContentKind.XHtml) ? await reader.ReadInnerXmlAsync() : await reader.ReadElementStringAsync();
            TextSyndicationContent result = new TextSyndicationContent(val, kind);
            if (attrs != null)
            {
                foreach (XmlQualifiedName attr in attrs.Keys)
                {
                    if (!FeedUtils.IsXmlns(attr.Name, attr.Namespace))
                    {
                        result.AttributeExtensions.Add(attr, attrs[attr]);
                    }
                }
            }
            return result;
        }

        private string AsString(DateTimeOffset dateTime)
        {
            if (dateTime.Offset == zeroOffset)
            {
                return dateTime.ToUniversalTime().ToString(Rfc3339UTCDateTimeFormat, CultureInfo.InvariantCulture);
            }
            else
            {
                return dateTime.ToString(Rfc3339LocalDateTimeFormat, CultureInfo.InvariantCulture);
            }
        }

        private DateTimeOffset DateFromString(string dateTimeString, XmlReaderWrapper reader)
        {
            dateTimeString = dateTimeString.Trim();
            if (dateTimeString.Length < 20)
            {
                //throw DiagnosticUtility.ExceptionUtility.ThrowHelperError(
                //    new XmlException(FeedUtils.AddLineInfo(reader,
                //    SR.ErrorParsingDateTime)));
                throw new XmlException(FeedUtils.AddLineInfo(reader, SR.ErrorParsingDateTime));
            }
            if (dateTimeString[19] == '.')
            {
                // remove any fractional seconds, we choose to ignore them
                int i = 20;
                while (dateTimeString.Length > i && char.IsDigit(dateTimeString[i]))
                {
                    ++i;
                }
                dateTimeString = dateTimeString.Substring(0, 19) + dateTimeString.Substring(i);
            }
            DateTimeOffset localTime;
            if (DateTimeOffset.TryParseExact(dateTimeString, Rfc3339LocalDateTimeFormat,
                CultureInfo.InvariantCulture.DateTimeFormat,
                DateTimeStyles.None, out localTime))
            {
                return localTime;
            }
            DateTimeOffset utcTime;
            if (DateTimeOffset.TryParseExact(dateTimeString, Rfc3339UTCDateTimeFormat,
                CultureInfo.InvariantCulture.DateTimeFormat,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utcTime))
            {
                return utcTime;
            }

            throw new XmlException(FeedUtils.AddLineInfo(reader, SR.ErrorParsingDateTime));
        }

        private async Task ReadCategory(XmlReaderWrapper reader, SyndicationCategory category)
        {
            await ReadCategoryAsync(reader, category, this.Version, this.PreserveAttributeExtensions, this.PreserveElementExtensions, _maxExtensionSize);
        }

        private async Task<SyndicationCategory> ReadCategoryFromAsync(XmlReaderWrapper reader, SyndicationFeed feed)
        {
            SyndicationCategory result = CreateCategory(feed);
            await ReadCategory(reader, result);
            return result;
        }

        private async Task<SyndicationCategory> ReadCategoryFromAsync(XmlReaderWrapper reader, SyndicationItem item)
        {
            SyndicationCategory result = CreateCategory(item);
            await ReadCategory(reader, result);
            return result;
        }

        private async Task<SyndicationContent> ReadContentFromAsync(XmlReaderWrapper reader, SyndicationItem item)
        {
            await MoveToStartElementAsync(reader);
            string type = reader.GetAttribute(Atom10Constants.TypeTag, string.Empty);

            SyndicationContent result;
            if (TryParseContent(reader, item, type, this.Version, out result))
            {
                return result;
            }

            if (string.IsNullOrEmpty(type))
            {
                type = Atom10Constants.PlaintextType;
            }
            string src = reader.GetAttribute(Atom10Constants.SourceTag, string.Empty);

            if (string.IsNullOrEmpty(src) && type != Atom10Constants.PlaintextType && type != Atom10Constants.HtmlType && type != Atom10Constants.XHtmlType)
            {
                return new XmlSyndicationContent(reader);
            }

            if (!string.IsNullOrEmpty(src))
            {
                result = new UrlSyndicationContent(new Uri(src, UriKind.RelativeOrAbsolute), type);
                bool isEmpty = reader.IsEmptyElement;
                if (reader.HasAttributes)
                {
                    while (reader.MoveToNextAttribute())
                    {
                        if (reader.LocalName == Atom10Constants.TypeTag && reader.NamespaceURI == string.Empty)
                        {
                            continue;
                        }
                        else if (reader.LocalName == Atom10Constants.SourceTag && reader.NamespaceURI == string.Empty)
                        {
                            continue;
                        }
                        else if (!FeedUtils.IsXmlns(reader.LocalName, reader.NamespaceURI))
                        {
                            if (_preserveAttributeExtensions)
                            {
                                result.AttributeExtensions.Add(new XmlQualifiedName(reader.LocalName, reader.NamespaceURI), await reader.GetValueAsync());
                            }
                        }
                    }
                }
                await reader.ReadStartElementAsync();
                if (!isEmpty)
                {
                    await reader.ReadEndElementAsync();
                }
                return result;
            }
            else
            {
                return await ReadTextContentFromHelper(reader, type, "//atom:feed/atom:entry/atom:content[@type]", _preserveAttributeExtensions);
            }
        }


        private async Task<SyndicationFeed> ReadFeedFromAsync(XmlReaderWrapper reader, SyndicationFeed result, bool isSourceFeed)
        {
            await reader.MoveToContentAsync();

            //fix to accept non contiguous items
            NullNotAllowedCollection<SyndicationItem> feedItems = new NullNotAllowedCollection<SyndicationItem>();

            bool elementIsEmpty = false;
            if (!isSourceFeed)
            {
                await MoveToStartElementAsync(reader);
                elementIsEmpty = reader.IsEmptyElement;
                if (reader.HasAttributes)
                {
                    while (reader.MoveToNextAttribute())
                    {
                        if (reader.LocalName == "lang" && reader.NamespaceURI == XmlNs)
                        {
                            result.Language = await reader.GetValueAsync();
                        }
                        else if (reader.LocalName == "base" && reader.NamespaceURI == XmlNs)
                        {
                            result.BaseUri = FeedUtils.CombineXmlBase(result.BaseUri, await reader.GetValueAsync());
                        }
                        else
                        {
                            string ns = reader.NamespaceURI;
                            string name = reader.LocalName;
                            if (FeedUtils.IsXmlns(name, ns) || FeedUtils.IsXmlSchemaType(name, ns))
                            {
                                continue;
                            }

                            string val = await reader.GetValueAsync();

                            if (!TryParseAttribute(name, ns, val, result, this.Version))
                            {
                                if (_preserveAttributeExtensions)
                                {
                                    result.AttributeExtensions.Add(new XmlQualifiedName(reader.LocalName, reader.NamespaceURI), await reader.GetValueAsync());
                                }
                            }
                        }
                    }
                }
                await reader.ReadStartElementAsync();
            }

            XmlBuffer buffer = null;
            XmlDictionaryWriter extWriter = null;
            bool areAllItemsRead = true;
            bool readItemsAtLeastOnce = false;

            if (!elementIsEmpty)
            {
                try
                {
                    while (await reader.IsStartElementAsync())
                    {
                        if (await TryParseFeedElementFromAsync(reader, result))  
                        {
                            // nothing, we parsed something, great
                        }
                        else if (await reader.IsStartElementAsync(Atom10Constants.EntryTag, Atom10Constants.Atom10Namespace) && !isSourceFeed)
                        {
                            if (readItemsAtLeastOnce)
                            {
                                //throw new InvalidOperationException(String.Format(SR.FeedHasNonContiguousItems, this.GetType().ToString()));
                                //Log we have disjoint items
                            }


                            while (await reader.IsStartElementAsync(Atom10Constants.EntryTag, Atom10Constants.Atom10Namespace))
                            {
                                feedItems.Add(await ReadItemAsync(reader, result));
                            }


                            areAllItemsRead = true;
                            readItemsAtLeastOnce = true;
                            // if the derived class is reading the items lazily, then stop reading from the stream
                            if (!areAllItemsRead)
                            {
                                break;
                            }
                        }
                        else
                        {
                            if (!TryParseElement(reader, result, this.Version))
                            {
                                if (_preserveElementExtensions)
                                {
                                    if (buffer == null)
                                    {
                                        buffer = new XmlBuffer(_maxExtensionSize);
                                        extWriter = buffer.OpenSection(XmlDictionaryReaderQuotas.Max);
                                        extWriter.WriteStartElement(Rss20Constants.ExtensionWrapperTag);
                                    }

                                    await XmlReaderWrapper.WriteNodeAsync(extWriter, reader, false);
                                }
                                else
                                {
                                    await reader.SkipAsync();
                                }
                            }
                        }
                    }
                    //Add all read items to the feed
                    result.Items = feedItems;
                    LoadElementExtensions(buffer, extWriter, result);  
                }
                finally
                {
                    if (extWriter != null)
                    {
                        ((IDisposable)extWriter).Dispose();
                    }
                }
            }
            if (!isSourceFeed && areAllItemsRead)
            {
                await reader.ReadEndElementAsync(); // feed
            }

            return result;
        }

        
        private async Task ReadItemFromAsync(XmlReaderWrapper reader, SyndicationItem result, Uri feedBaseUri)
        {
            try
            {
                result.BaseUri = feedBaseUri;
                await MoveToStartElementAsync(reader);
                bool isEmpty = reader.IsEmptyElement;
                if (reader.HasAttributes)
                {
                    while (reader.MoveToNextAttribute())
                    {
                        string ns = reader.NamespaceURI;
                        string name = reader.LocalName;
                        if (name == "base" && ns == XmlNs)
                        {
                            result.BaseUri = FeedUtils.CombineXmlBase(result.BaseUri, await reader.GetValueAsync());
                            continue;
                        }

                        if (FeedUtils.IsXmlns(name, ns) || FeedUtils.IsXmlSchemaType(name, ns))
                        {
                            continue;
                        }

                        string val = await reader.GetValueAsync();
                        if (!TryParseAttribute(name, ns, val, result, this.Version))
                        {
                            if (_preserveAttributeExtensions)
                            {
                                result.AttributeExtensions.Add(new XmlQualifiedName(reader.LocalName, reader.NamespaceURI), reader.Value);
                            }
                        }
                    }
                }
                await reader.ReadStartElementAsync();

                if (!isEmpty)
                {
                    XmlBuffer buffer = null;
                    XmlDictionaryWriter extWriter = null;
                    try
                    {
                        while (await reader.IsStartElementAsync())
                        {
                            if (await TryParseItemElementFromAsync(reader, result))
                            {
                                // nothing, we parsed something, great
                            }
                            else
                            {
                                if (!TryParseElement(reader, result, this.Version))
                                {
                                    if (_preserveElementExtensions)
                                    {
                                        if (buffer == null)
                                        {
                                            buffer = new XmlBuffer(_maxExtensionSize);
                                            extWriter = buffer.OpenSection(XmlDictionaryReaderQuotas.Max);
                                            extWriter.WriteStartElement(Rss20Constants.ExtensionWrapperTag);
                                        }

                                        await XmlReaderWrapper.WriteNodeAsync(extWriter, reader, false);
                                    }
                                    else
                                    {
                                        await reader.SkipAsync();
                                    }
                                }
                            }
                        }
                        LoadElementExtensions(buffer, extWriter, result);
                    }
                    finally
                    {
                        if (extWriter != null)
                        {
                            ((IDisposable)extWriter).Dispose();
                        }
                    }
                    await reader.ReadEndElementAsync(); // item
                }
            }
            catch (FormatException e)
            {
                throw new XmlException(FeedUtils.AddLineInfo(reader, SR.ErrorParsingItem), e);
               
            }
            catch (ArgumentException e)
            {
                throw new XmlException(FeedUtils.AddLineInfo(reader, SR.ErrorParsingItem), e);
            }
        }

        private async Task ReadLinkAsync(XmlReaderWrapper reader, SyndicationLink link, Uri baseUri)
        {
            bool isEmpty = reader.IsEmptyElement;
            string mediaType = null;
            string relationship = null;
            string title = null;
            string lengthStr = null;
            string val = null;
            link.BaseUri = baseUri;
            if (reader.HasAttributes)
            {
                while (reader.MoveToNextAttribute())
                {
                    bool notHandled = false;
                    if (reader.LocalName == "base" && reader.NamespaceURI == XmlNs)
                    {
                        link.BaseUri = FeedUtils.CombineXmlBase(link.BaseUri, await reader.GetValueAsync());
                    }
                    else if(reader.NamespaceURI == string.Empty)
                    {
                        switch (reader.LocalName)
                        {
                            case Atom10Constants.TypeTag:
                                mediaType = await reader.GetValueAsync();
                                break;

                            case Atom10Constants.RelativeTag:
                                relationship = await reader.GetValueAsync();
                                break;

                            case Atom10Constants.TitleTag:
                                title = await reader.GetValueAsync();
                                break;

                            case Atom10Constants.LengthTag:
                                lengthStr = await reader.GetValueAsync();
                                break;

                            case Atom10Constants.HrefTag:
                                val = await reader.GetValueAsync();
                                break;

                            default:
                                notHandled = true;
                                break;
                        }
                    }
                    else
                    {
                        notHandled = true;
                    }

                    if (notHandled && !FeedUtils.IsXmlns(reader.LocalName, reader.NamespaceURI))
                    {
                        if (_preserveAttributeExtensions)
                        {
                            link.AttributeExtensions.Add(new XmlQualifiedName(reader.LocalName, reader.NamespaceURI), await reader.GetValueAsync());
                        }
                    }

                    //if (reader.LocalName == "base" && reader.NamespaceURI == XmlNs)
                    //{
                    //    link.BaseUri = FeedUtils.CombineXmlBase(link.BaseUri, await reader.GetValueAsync());
                    //}
                    //else if (reader.LocalName == Atom10Constants.TypeTag && reader.NamespaceURI == string.Empty)
                    //{
                    //    mediaType = await reader.GetValueAsync();
                    //}
                    //else if (reader.LocalName == Atom10Constants.RelativeTag && reader.NamespaceURI == string.Empty)
                    //{
                    //    relationship = await reader.GetValueAsync();
                    //}
                    //else if (reader.LocalName == Atom10Constants.TitleTag && reader.NamespaceURI == string.Empty)
                    //{
                    //    title = await reader.GetValueAsync();
                    //}
                    //else if (reader.LocalName == Atom10Constants.LengthTag && reader.NamespaceURI == string.Empty)
                    //{
                    //    lengthStr = await reader.GetValueAsync();
                    //}
                    //else if (reader.LocalName == Atom10Constants.HrefTag && reader.NamespaceURI == string.Empty)
                    //{
                    //    val = await reader.GetValueAsync();
                    //}
                    //else if (!FeedUtils.IsXmlns(reader.LocalName, reader.NamespaceURI))
                    //{
                    //    if (_preserveAttributeExtensions)
                    //    {
                    //        link.AttributeExtensions.Add(new XmlQualifiedName(reader.LocalName, reader.NamespaceURI), await reader.GetValueAsync());
                    //    }
                    //}
                }
            }

            long length = 0;
            if (!string.IsNullOrEmpty(lengthStr))
            {
                length = Convert.ToInt64(lengthStr, CultureInfo.InvariantCulture.NumberFormat);
            }

            await reader.ReadStartElementAsync();
            if (!isEmpty)
            {
                XmlBuffer buffer = null;
                XmlDictionaryWriter extWriter = null;
                try
                {
                    while (await reader.IsStartElementAsync())
                    {
                        if (TryParseElement(reader, link, this.Version))
                        {
                            continue;
                        }
                        else if (!_preserveElementExtensions)
                        {
                            await reader.SkipAsync();
                        }
                        else
                        {
                            if (buffer == null)
                            {
                                buffer = new XmlBuffer(_maxExtensionSize);
                                extWriter = buffer.OpenSection(XmlDictionaryReaderQuotas.Max);
                                extWriter.WriteStartElement(Rss20Constants.ExtensionWrapperTag);
                            }

                            await XmlReaderWrapper.WriteNodeAsync(extWriter, reader, false);
                        }
                    }
                    LoadElementExtensions(buffer, extWriter, link);
                }
                finally
                {
                    if (extWriter != null)
                    {
                        ((IDisposable)extWriter).Dispose();
                    }
                }
                await reader.ReadEndElementAsync();
            }
            link.Length = length;
            link.MediaType = mediaType;
            link.RelationshipType = relationship;
            link.Title = title;
            link.Uri = (val != null) ? new Uri(val, UriKind.RelativeOrAbsolute) : null;
        }

        private async Task<SyndicationLink> ReadLinkFromAsync(XmlReaderWrapper reader, SyndicationFeed feed)
        {
            SyndicationLink result = CreateLink(feed);
            await ReadLinkAsync(reader, result, feed.BaseUri);
            return result;
        }

        private async Task<SyndicationLink> ReadLinkFromAsync(XmlReaderWrapper reader, SyndicationItem item)
        {
            SyndicationLink result = CreateLink(item);
            await ReadLinkAsync(reader, result, item.BaseUri);
            return result;
        }

        private async Task<SyndicationPerson> ReadPersonFromAsync(XmlReaderWrapper reader, SyndicationFeed feed)
        {
            SyndicationPerson result = CreatePerson(feed);
            await ReadPersonFromAsync(reader, result);
            return result;
        }

        private async Task<SyndicationPerson> ReadPersonFromAsync(XmlReaderWrapper reader, SyndicationItem item)
        {
            SyndicationPerson result = CreatePerson(item);
            await ReadPersonFromAsync(reader, result);
            return result;
        }

        //private async Task ReadPersonFromAsync(XmlReaderWrapper reader, SyndicationPerson result)
        //{
        //    bool isEmpty = reader.IsEmptyElement;
        //    if (reader.HasAttributes)
        //    {
        //        while (reader.MoveToNextAttribute())
        //        {
        //            string ns = reader.NamespaceURI;
        //            string name = reader.LocalName;
        //            if (FeedUtils.IsXmlns(name, ns))
        //            {
        //                continue;
        //            }
        //            string val = await reader.GetValueAsync();
        //            if (!TryParseAttribute(name, ns, val, result, this.Version))
        //            {
        //                if (_preserveAttributeExtensions)
        //                {
        //                    result.AttributeExtensions.Add(new XmlQualifiedName(reader.LocalName, reader.NamespaceURI), await reader.GetValueAsync());
        //                }
        //            }
        //        }
        //    }
        //    await reader.ReadStartElementAsync();
        //    if (!isEmpty)
        //    {
        //        XmlBuffer buffer = null;
        //        XmlDictionaryWriter extWriter = null;
        //        try
        //        {
        //            while (await reader.IsStartElementAsync())
        //            {
        //                if (await reader.IsStartElementAsync(Atom10Constants.NameTag, Atom10Constants.Atom10Namespace))
        //                {
        //                    result.Name = await reader.ReadElementStringAsync();
        //                }
        //                else if (await reader.IsStartElementAsync(Atom10Constants.UriTag, Atom10Constants.Atom10Namespace))
        //                {
        //                    result.Uri = await reader.ReadElementStringAsync();
        //                }
        //                else if (await reader.IsStartElementAsync(Atom10Constants.EmailTag, Atom10Constants.Atom10Namespace))
        //                {
        //                    result.Email = await reader.ReadElementStringAsync();
        //                }
        //                else
        //                {
        //                    if (!TryParseElement(reader, result, this.Version))
        //                    {
        //                        if (_preserveElementExtensions)
        //                        {
        //                            if (buffer == null)
        //                            {
        //                                buffer = new XmlBuffer(_maxExtensionSize);
        //                                extWriter = buffer.OpenSection(XmlDictionaryReaderQuotas.Max);
        //                                extWriter.WriteStartElement(Rss20Constants.ExtensionWrapperTag);
        //                            }

        //                            await XmlReaderWrapper.WriteNodeAsync(extWriter, reader, false);
        //                        }
        //                        else
        //                        {
        //                            await reader.SkipAsync();
        //                        }
        //                    }
        //                }
        //            }
        //            LoadElementExtensions(buffer, extWriter, result);
        //        }
        //        finally
        //        {
        //            if (extWriter != null)
        //            {
        //                ((IDisposable)extWriter).Dispose();
        //            }
        //        }
        //        await reader.ReadEndElementAsync();
        //    }
        //}

        private async Task ReadPersonFromAsync(XmlReaderWrapper reader, SyndicationPerson result)
        {
            bool isEmpty = reader.IsEmptyElement;
            if (reader.HasAttributes)
            {
                while (reader.MoveToNextAttribute())
                {
                    string ns = reader.NamespaceURI;
                    string name = reader.LocalName;
                    if (FeedUtils.IsXmlns(name, ns))
                    {
                        continue;
                    }
                    string val = await reader.GetValueAsync();
                    if (!TryParseAttribute(name, ns, val, result, this.Version))
                    {
                        if (_preserveAttributeExtensions)
                        {
                            result.AttributeExtensions.Add(new XmlQualifiedName(reader.LocalName, reader.NamespaceURI), await reader.GetValueAsync());
                        }
                    }
                }
            }
            await reader.ReadStartElementAsync();
            if (!isEmpty)
            {
                XmlBuffer buffer = null;
                XmlDictionaryWriter extWriter = null;
                try
                {
                    while (await reader.IsStartElementAsync())
                    {

                        string name = reader.LocalName;
                        string ns = reader.NamespaceURI;
                        bool notHandled = false;


                        switch (name)
                        {
                            case Atom10Constants.NameTag:
                            result.Name = await reader.ReadElementStringAsync();
                                break;
                            case Atom10Constants.UriTag:
                            result.Uri = await reader.ReadElementStringAsync();
                                break;
                            case Atom10Constants.EmailTag:
                            result.Email = await reader.ReadElementStringAsync();
                                break;
                            default:
                                notHandled = true;
                                break;
                        }

                        if(notHandled && !TryParseElement(reader, result, this.Version))
                        {
                            if (_preserveElementExtensions)
                            {
                                if (buffer == null)
                                {
                                    buffer = new XmlBuffer(_maxExtensionSize);
                                    extWriter = buffer.OpenSection(XmlDictionaryReaderQuotas.Max);
                                    extWriter.WriteStartElement(Rss20Constants.ExtensionWrapperTag);
                                }

                                await XmlReaderWrapper.WriteNodeAsync(extWriter, reader, false);
                            }
                            else
                            {
                                await reader.SkipAsync();
                            }
                        }                        
                    }

                    LoadElementExtensions(buffer, extWriter, result);
                }
                finally
                {
                    if (extWriter != null)
                    {
                        ((IDisposable)extWriter).Dispose();
                    }
                }
                await reader.ReadEndElementAsync();
            }
        }

        private async Task<TextSyndicationContent> ReadTextContentFromAsync(XmlReaderWrapper reader, string context)
        {
            return await ReadTextContentFromAsync(reader, context, this.PreserveAttributeExtensions);
        }

        private void WriteCategoriesTo(XmlWriter writer, Collection<SyndicationCategory> categories)
        {
            for (int i = 0; i < categories.Count; ++i)
            {
                WriteCategory(writer, categories[i], this.Version);
            }
        }

        private void WriteFeed(XmlWriter writer)
        {
            if (this.Feed == null)
            {
                throw new InvalidOperationException(SR.FeedFormatterDoesNotHaveFeed);
            }
            WriteFeedTo(writer, this.Feed, false); //  isSourceFeed 
        }

        private void WriteFeedTo(XmlWriter writer, SyndicationFeed feed, bool isSourceFeed)
        {
            if (!isSourceFeed)
            {
                if (!string.IsNullOrEmpty(feed.Language))
                {
                    writer.WriteAttributeString("xml", "lang", XmlNs, feed.Language);
                }
                if (feed.BaseUri != null)
                {
                    writer.WriteAttributeString("xml", "base", XmlNs, FeedUtils.GetUriString(feed.BaseUri));
                }
                WriteAttributeExtensions(writer, feed, this.Version);
            }
            bool isElementRequired = !isSourceFeed;
            TextSyndicationContent title = feed.Title;
            if (isElementRequired)
            {
                title = title ?? new TextSyndicationContent(string.Empty);
            }
            WriteContentTo(writer, Atom10Constants.TitleTag, title);
            WriteContentTo(writer, Atom10Constants.SubtitleTag, feed.Description);
            string id = feed.Id;
            if (isElementRequired)
            {
                id = id ?? s_idGenerator.Next();
            }
            WriteElement(writer, Atom10Constants.IdTag, id);
            WriteContentTo(writer, Atom10Constants.RightsTag, feed.Copyright);
            WriteFeedLastUpdatedTimeTo(writer, feed.LastUpdatedTime, isElementRequired);
            WriteCategoriesTo(writer, feed.Categories);
            if (feed.ImageUrl != null)
            {
                WriteElement(writer, Atom10Constants.LogoTag, feed.ImageUrl.ToString());
            }
            WriteFeedAuthorsTo(writer, feed.Authors);
            WriteFeedContributorsTo(writer, feed.Contributors);
            WriteElement(writer, Atom10Constants.GeneratorTag, feed.Generator);

            for (int i = 0; i < feed.Links.Count; ++i)
            {
                WriteLink(writer, feed.Links[i], feed.BaseUri);
            }

            WriteElementExtensions(writer, feed, this.Version);

            if (!isSourceFeed)
            {
                WriteItems(writer, feed.Items, feed.BaseUri);
            }
        }

        private void WriteItemContents(XmlWriter dictWriter, SyndicationItem item, Uri feedBaseUri)
        {
            Uri baseUriToWrite = FeedUtils.GetBaseUriToWrite(feedBaseUri, item.BaseUri);
            if (baseUriToWrite != null)
            {
                dictWriter.WriteAttributeString("xml", "base", XmlNs, FeedUtils.GetUriString(baseUriToWrite));
            }
            WriteAttributeExtensions(dictWriter, item, this.Version);

            string id = item.Id ?? s_idGenerator.Next();
            WriteElement(dictWriter, Atom10Constants.IdTag, id);

            TextSyndicationContent title = item.Title ?? new TextSyndicationContent(string.Empty);
            WriteContentTo(dictWriter, Atom10Constants.TitleTag, title);
            WriteContentTo(dictWriter, Atom10Constants.SummaryTag, item.Summary);
            if (item.PublishDate != DateTimeOffset.MinValue)
            {
                dictWriter.WriteElementString(Atom10Constants.PublishedTag,
                    Atom10Constants.Atom10Namespace,
                    AsString(item.PublishDate));
            }
            WriteItemLastUpdatedTimeTo(dictWriter, item.LastUpdatedTime);
            WriteItemAuthorsTo(dictWriter, item.Authors);
            WriteItemContributorsTo(dictWriter, item.Contributors);
            for (int i = 0; i < item.Links.Count; ++i)
            {
                WriteLink(dictWriter, item.Links[i], item.BaseUri);
            }
            WriteCategoriesTo(dictWriter, item.Categories);
            WriteContentTo(dictWriter, Atom10Constants.ContentTag, item.Content);
            WriteContentTo(dictWriter, Atom10Constants.RightsTag, item.Copyright);
            if (item.SourceFeed != null)
            {
                dictWriter.WriteStartElement(Atom10Constants.SourceFeedTag, Atom10Constants.Atom10Namespace);
                WriteFeedTo(dictWriter, item.SourceFeed, true); //  isSourceFeed 
                dictWriter.WriteEndElement();
            }
            WriteElementExtensions(dictWriter, item, this.Version);
        }

        private void WritePersonTo(XmlWriter writer, SyndicationPerson p, string elementName)
        {
            writer.WriteStartElement(elementName, Atom10Constants.Atom10Namespace);
            WriteAttributeExtensions(writer, p, this.Version);
            WriteElement(writer, Atom10Constants.NameTag, p.Name);
            if (!string.IsNullOrEmpty(p.Uri))
            {
                writer.WriteElementString(Atom10Constants.UriTag, Atom10Constants.Atom10Namespace, p.Uri);
            }
            if (!string.IsNullOrEmpty(p.Email))
            {
                writer.WriteElementString(Atom10Constants.EmailTag, Atom10Constants.Atom10Namespace, p.Email);
            }
            WriteElementExtensions(writer, p, this.Version);
            writer.WriteEndElement();
        }
    }

    [XmlRoot(ElementName = Atom10Constants.FeedTag, Namespace = Atom10Constants.Atom10Namespace)]
    public class Atom10FeedFormatter<TSyndicationFeed> : Atom10FeedFormatter
        where TSyndicationFeed : SyndicationFeed, new()
    {
        // constructors
        public Atom10FeedFormatter()
            : base(typeof(TSyndicationFeed))
        {
        }
        public Atom10FeedFormatter(TSyndicationFeed feedToWrite)
            : base(feedToWrite)
        {
        }

        protected override SyndicationFeed CreateFeedInstance()
        {
            return new TSyndicationFeed();
        }
    }
}
