﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml.Linq;
using System.Xml.XPath;
using Microsoft.OpenApiSpecification.Core.Models;
using Microsoft.OpenApiSpecification.Generation.ConfigFilters;
using Microsoft.OpenApiSpecification.Generation.DocumentConfigFilters;
using Microsoft.OpenApiSpecification.Generation.DocumentFilters;
using Microsoft.OpenApiSpecification.Generation.Exceptions;
using Microsoft.OpenApiSpecification.Generation.Extensions;
using Microsoft.OpenApiSpecification.Generation.Models;
using Microsoft.OpenApiSpecification.Generation.Models.KnownStrings;
using Microsoft.OpenApiSpecification.Generation.OperationFilters;
using Newtonsoft.Json;

namespace Microsoft.OpenApiSpecification.Generation
{
    /// <summary>
    /// Provides functionality to parse xml into OpenApiV3Specification
    /// </summary>
    internal class InternalOpenApiDocumentGenerator : MarshalByRefObject
    {
        private static readonly IList<IDocumentConfigFilter> _defaultDocumentConfigFilters =
            new List<IDocumentConfigFilter>
            {
                new ApplyDocumentVariantAttributesFilter()
            };

        private static readonly IList<IDocumentFilter> _defaultDocumentFilters = new List<IDocumentFilter>
        {
            new ApplyAssemblyNameAsInfoFilter(),
            new ApplyUrlAsServerFilter(),
            new ApplyMemberSummaryAsSchemaDescriptionFilter()
        };

        private static readonly IList<IOperationConfigFilter> _defaultOperationConfigFilters =
            new List<IOperationConfigFilter>
            {
                new ApplyCommonAnnotationFilter()
            };

        private static readonly IList<IOperationFilter> _defaultOperationFilters = new List<IOperationFilter>
        {
            new ApplyGroupAsTagFilter(),
            new ApplyParamAsParameterFilter(),
            new ApplyParamAsRequestBodyFilter(),
            new ApplyResponseAsResponseFilter(),
            new ApplyRemarksAsDescriptionFilter(),
            new ApplySummaryFilter()
        };

        // TO DO: Figure out a way to serialize this and pass as parameter from OpenApiDocumentGenerator.
        private readonly OpenApiDocumentGeneratorConfig _generatorConfig = new OpenApiDocumentGeneratorConfig(
            _defaultOperationFilters,
            _defaultDocumentFilters,
            _defaultOperationConfigFilters,
            _defaultDocumentConfigFilters);

        /// <summary>
        /// Add operation and update the operation filter settings based on the given document variant info.
        /// </summary>
        private void AddOperation(
            IDictionary<DocumentVariantInfo, OpenApiV3SpecificationDocument> specificationDocuments,
            IDictionary<DocumentVariantInfo, OperationFilterSettings> operationFilterSettingsMap,
            DocumentVariantInfo documentVariantInfo,
            XElement operationElement,
            XElement operationConfigElement,
            string url,
            OperationMethod operationMethod,
            TypeFetcher typeFetcher)
        {
            var operation = new Operation
            {
                OperationId = GetOperationId(url, operationMethod)
            };

            if (!operationFilterSettingsMap.ContainsKey(documentVariantInfo))
            {
                operationFilterSettingsMap[documentVariantInfo] =
                    new OperationFilterSettings
                    {
                        TypeFetcher = typeFetcher
                    };
            }

            // Apply all the operation-related filters to extract information related to the operation.
            // It is important that these are applied before the config filters below
            // since the config filters may rely on information generated from operation filters.
            foreach (var operationFilter in _generatorConfig.OperationFilters)
            {
                operationFilter.Apply(
                    operation,
                    operationElement,
                    operationFilterSettingsMap[documentVariantInfo]);
            }

            if (operationConfigElement != null)
            {
                // Apply the config-related filters to extract information from the config xml
                // that can be applied to the operations.
                foreach (var configFilter in _generatorConfig.OperationConfigFilters)
                {
                    configFilter.Apply(
                        operation,
                        operationConfigElement,
                        new OperationConfigFilterSettings
                        {
                            OperationFilterSettings = operationFilterSettingsMap[documentVariantInfo],
                            OperationFilters = _generatorConfig.OperationFilters
                        });
                }
            }

            // Add the processed operation to the specification document.
            if (!specificationDocuments.ContainsKey(documentVariantInfo))
            {
                specificationDocuments.Add(
                    documentVariantInfo,
                    new OpenApiV3SpecificationDocument());
            }

            var paths = specificationDocuments[documentVariantInfo].Paths;

            if (!paths.ContainsKey(url))
            {
                var pathItem = new PathItem
                {
                    [operationMethod] = operation
                };

                paths.Add(url, pathItem);
            }
            else
            {
                paths[url].Add(operationMethod, operation);
            }
        }

        /// <summary>
        /// Takes in annotation xml document and returns the OpenAPI document generation result
        /// which contains OpenAPI specification document(s).
        /// </summary>
        /// <param name="annotationXml">The serialized XDocument representing annotation.</param>
        /// <param name="contractAssemblyPaths">The contract assembly paths.</param>
        /// <param name="configurationXml">The serialized XDocument representing the generation configuration.</param>
        /// <returns>A string representing serialized version of <see cref="DocumentGenerationResult"/>></returns>
        /// <remarks>
        /// Given that this function is expected to be called from an isolated domain,
        /// the input and output must be serialized to string.
        /// </remarks>
        public string GenerateOpenApiDocuments(
            string annotationXml,
            IList<string> contractAssemblyPaths,
            string configurationXml)
        {
            var annotationXmlDocument = XDocument.Parse(annotationXml);
            var operationElements = annotationXmlDocument.XPathSelectElements("//doc/members/member[url and verb]")
                .ToList();

            XElement operationConfigElement = null;

            XElement documentConfigElement = null;

            var documentVariantElementNames = new List<string>();

            if (!string.IsNullOrWhiteSpace(configurationXml))
            {
                var configurationXmlDocument = XDocument.Parse(configurationXml);

                operationConfigElement = configurationXmlDocument.XPathSelectElement("//configuration/operation");
                documentConfigElement = configurationXmlDocument.XPathSelectElement("//configuration/document");
                documentVariantElementNames = configurationXmlDocument
                    .XPathSelectElements("//configuration/document/variant/name")
                    .Select(variantName => variantName.Value)
                    .ToList();
            }

            DocumentGenerationResult result;

            if (!operationElements.Any())
            {
                result = new DocumentGenerationResult(
                    new List<PathGenerationResult>
                    {
                        new PathGenerationResult
                        {
                            Message = SpecificationGenerationMessages.NoOperationElementFoundToParse,
                            GenerationStatus = GenerationStatus.Success
                        }
                    });

                return JsonConvert.SerializeObject(result);
            }

            try
            {
                result = new DocumentGenerationResult();

                var typeFetcher = new TypeFetcher(contractAssemblyPaths);

                IDictionary<DocumentVariantInfo, OpenApiV3SpecificationDocument> documents;

                var pathGenerationResults = GenerateSpecificationDocuments(
                    typeFetcher,
                    operationElements,
                    operationConfigElement,
                    documentVariantElementNames,
                    out documents);

                result.Documents = documents;

                foreach (var pathGenerationResult in pathGenerationResults)
                {
                    result.PathGenerationResults.Add(new PathGenerationResult(pathGenerationResult));
                }

                try
                {
                    foreach (var documentVariantInfo in result.Documents.Keys)
                    {
                        var openApiDocument = result.Documents[documentVariantInfo];

                        foreach (var documentFilter in _generatorConfig.DocumentFilters)
                        {
                            documentFilter.Apply(
                                openApiDocument,
                                annotationXmlDocument,
                                new DocumentFilterSettings
                                {
                                    TypeFetcher = typeFetcher
                                });
                        }
                    }

                    if (documentConfigElement != null)
                    {
                        foreach (var documentConfigFilter in _generatorConfig.DocumentConfigFilters)
                        {
                            documentConfigFilter.Apply(
                                documents,
                                documentConfigElement,
                                annotationXmlDocument,
                                new DocumentConfigFilterSettings());
                        }
                    }
                }
                catch (Exception e)
                {
                    // Document and document config filters yield an exception.
                    // This exception may not be tied to a particular operation and the resulting
                    // documents may be in bad state. We simply return empty document with
                    // an exception message in the path generation result.
                    result = new DocumentGenerationResult(
                        new List<PathGenerationResult>
                        {
                            new PathGenerationResult
                            {
                                ExceptionType = e.GetType(),
                                Message = e.Message,
                                GenerationStatus = GenerationStatus.Failure
                            }
                        });
                }

                return JsonConvert.SerializeObject(result);
            }
            catch (Exception e)
            {
                result = new DocumentGenerationResult(
                    new List<PathGenerationResult>
                    {
                        new PathGenerationResult
                        {
                            ExceptionType = e.GetType(),
                            Message = string.Format(SpecificationGenerationMessages.UnexpectedError, e),
                            GenerationStatus = GenerationStatus.Failure
                        }
                    });

                return JsonConvert.SerializeObject(result);
            }
        }

        /// <summary>
        /// Populate the specification documents for all document variant infos.
        /// </summary>
        /// <returns>The path generation results from populating the specification documents.</returns>
        private IList<PathGenerationResult> GenerateSpecificationDocuments(
            TypeFetcher typeFetcher,
            IList<XElement> operationElements,
            XElement operationConfigElement,
            IList<string> documentVariantElementNames,
            out IDictionary<DocumentVariantInfo, OpenApiV3SpecificationDocument> specificationDocuments)
        {
            specificationDocuments = new Dictionary<DocumentVariantInfo, OpenApiV3SpecificationDocument>();

            var pathGenerationResults = new List<PathGenerationResult>();

            var operationFilterSettingsMap = new Dictionary<DocumentVariantInfo, OperationFilterSettings>();

            foreach (var operationElement in operationElements)
            {
                string url;
                OperationMethod operationMethod;

                try
                {
                    url = GetUrl(operationElement);
                }
                catch (InvalidUrlException e)
                {
                    pathGenerationResults.Add(
                        new PathGenerationResult
                        {
                            OperationMethod = SpecificationGenerationMessages.OperationMethodNotParsedGivenUrlIsInvalid,
                            Path = e.Url,
                            ExceptionType = e.GetType(),
                            Message = e.Message,
                            GenerationStatus = GenerationStatus.Failure
                        });

                    continue;
                }

                try
                {
                    operationMethod = GetOperationMethod(url, operationElement);
                }
                catch (InvalidVerbException e)
                {
                    pathGenerationResults.Add(
                        new PathGenerationResult
                        {
                            OperationMethod = e.Verb,
                            Path = url,
                            ExceptionType = e.GetType(),
                            Message = e.Message,
                            GenerationStatus = GenerationStatus.Failure
                        });

                    continue;
                }

                try
                {
                    AddOperation(
                        specificationDocuments,
                        operationFilterSettingsMap,
                        DocumentVariantInfo.Default,
                        operationElement,
                        operationConfigElement,
                        url,
                        operationMethod,
                        typeFetcher);

                    foreach (var documentVariantElementName in documentVariantElementNames)
                    {
                        var customElements = operationElement.Descendants(documentVariantElementName);
                        foreach (var customElement in customElements)
                        {
                            var documentVariantInfo = new DocumentVariantInfo
                            {
                                Categorizer = customElement.Name.LocalName.Trim(),
                                Title = customElement.Value.Trim()
                            };

                            AddOperation(
                                specificationDocuments,
                                operationFilterSettingsMap,
                                documentVariantInfo,
                                operationElement,
                                operationConfigElement,
                                url,
                                operationMethod,
                                typeFetcher);
                        }
                    }

                    pathGenerationResults.Add(
                        new PathGenerationResult
                        {
                            OperationMethod = operationMethod.ToString(),
                            Path = url,
                            Message = SpecificationGenerationMessages.SuccessfulPathGeneration,
                            GenerationStatus = GenerationStatus.Success
                        });
                }
                catch (Exception e)
                {
                    pathGenerationResults.Add(
                        new PathGenerationResult
                        {
                            OperationMethod = operationMethod.ToString(),
                            Path = url,
                            ExceptionType = e.GetType(),
                            Message = e.Message,
                            GenerationStatus = GenerationStatus.Failure
                        });
                }
            }

            foreach (var documentVariantInfo in specificationDocuments.Keys)
            {
                operationFilterSettingsMap[documentVariantInfo]
                    .ReferenceRegistryManager
                    .SchemaReferenceRegistry.References.CopyInto(
                        specificationDocuments[documentVariantInfo].Components.Schemas);
            }

            return pathGenerationResults;
        }

        private string GetOperationId(string absolutePath, OperationMethod operationMethod)
        {
            if (string.IsNullOrEmpty(absolutePath))
            {
                throw new ArgumentException(nameof(absolutePath) + " must be specified");
            }

            var operationId = new StringBuilder(operationMethod.ToString().ToLowerInvariant());

            foreach (var segment in absolutePath.Split('/'))
            {
                if (string.IsNullOrEmpty(segment))
                {
                    continue;
                }

                var current = string.Empty;

                // In order to build an operation id, extract the path parameters
                // and prepend By to these before adding them to the operationId.
                // e.g. for GET /v6/products/{productId} -> getV6ProductsByProductId 
                if (segment.Contains("{"))
                {
                    var matches = new Regex(@"\{(.*?)\}").Matches(segment);

                    if (matches.Count > 0)
                    {
                        foreach (Match match in matches)
                        {
                            current += "By" + match.Groups[1].Value.ToTitleCase();
                        }
                    }
                }
                else
                {
                    current = segment.ToTitleCase();
                }

                // Open api spec recommends to follow common programming naming conventions for operation Id
                // So only allow alphabets or alphanumerics.
                operationId.Append(Regex.Replace(current, "[^a-zA-Z0-9]", string.Empty));
            }

            return operationId.ToString();
        }

        /// <summary>
        /// Extracts the operation method from the operation element
        /// </summary>
        /// <exception cref="InvalidVerbException">Thrown if the verb is missing or has invalid format.</exception>
        private static OperationMethod GetOperationMethod(
            string url,
            XElement operationElement)
        {
            var verbElement = operationElement.Descendants().FirstOrDefault(i => i.Name == KnownXmlStrings.Verb);

            var verb = verbElement?.Value.Trim();

            OperationMethod operationMethod;

            if (Enum.TryParse(verb, true, out operationMethod))
            {
                return operationMethod;
            }

            throw new InvalidVerbException(verb);
        }

        /// <summary>
        /// Extracts the URL from the operation element
        /// </summary>
        /// <exception cref="InvalidUrlException">Thrown if the URL is missing or has invalid format.</exception>
        private static string GetUrl(
            XElement operationElement)
        {
            var urls = operationElement.Elements(KnownXmlStrings.Url).Select(i => i.Value);

            // Can't process further if no url is documented, so skip the operation.
            var url = urls.FirstOrDefault();

            if (url == null)
            {
                throw new InvalidUrlException(
                    url,
                    SpecificationGenerationMessages.NullUrl);
            }

            try
            {
                url = HttpUtility.UrlDecode(new Uri(url).AbsolutePath);
            }
            catch (UriFormatException)
            {
                throw new InvalidUrlException(
                    url,
                    SpecificationGenerationMessages.MalformattedUrl);
            }
            catch (Exception e)
            {
                throw new InvalidUrlException(
                    url,
                    e.Message);
            }

            return url;
        }
    }
}