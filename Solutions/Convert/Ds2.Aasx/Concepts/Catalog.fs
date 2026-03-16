namespace Ds2.Aasx

open System.Collections.Generic
open AasCore.Aas3_0

module internal AasxConceptDescriptionCatalog =

    type ConceptDescriptionInfo = {
        Id: string                      // IRDI (예: 0173-1#02-AAO677#002)
        PreferredNameDe: string         // 독일어 이름
        PreferredNameEn: string         // 영어 이름
        ShortName: string               // 짧은 이름
        DefinitionDe: string            // 독일어 정의
        DefinitionEn: string            // 영어 정의
    }

    /// ECLASS IRDI -> ConceptDescription 정보 매핑
    let conceptDescriptionInfos: ConceptDescriptionInfo list = [
        // === Digital Nameplate (IDTA 02006-3-0) ===
        { Id = "0173-1#02-AAY811#001"
          PreferredNameDe = "URI des Produktes"
          PreferredNameEn = "URI of the product"
          ShortName = "URIProd"
          DefinitionDe = "Eindeutige Identifikation des Produkts durch einen URI"
          DefinitionEn = "Unique product identification via URI" }

        { Id = "0173-1#02-AAO677#002"
          PreferredNameDe = "Herstellername"
          PreferredNameEn = "Manufacturer name"
          ShortName = "ManNam"
          DefinitionDe = "Bezeichnung für eine natürliche oder juristische Person, die für die Auslegung, Herstellung und Verpackung sowie die Etikettierung eines Produkts im Hinblick auf das 'Inverkehrbringen' im eigenen Namen verantwortlich ist"
          DefinitionEn = "legally valid designation of the natural or judicial person which is directly responsible for the design, production, packaging and labeling of a product in respect to its being brought into circulation" }

        { Id = "0173-1#02-AAW338#001"
          PreferredNameDe = "Herstellerproduktbezeichnung"
          PreferredNameEn = "Manufacturer product designation"
          ShortName = "ManProDes"
          DefinitionDe = "Kurzbezeichnung des Produkts, welche durch den Hersteller vergeben wird"
          DefinitionEn = "Short description of the product, provided by the manufacturer" }

        { Id = "0173-1#02-AAO227#002"
          PreferredNameDe = "Bestellnummer des Herstellers"
          PreferredNameEn = "Order code of manufacturer"
          ShortName = "OrdCodMan"
          DefinitionDe = "Durch den Hersteller vergebene Nummer, die Gegenstand einer Bestellung ist"
          DefinitionEn = "Number assigned by the manufacturer which is the subject of an order" }

        { Id = "0173-1#02-AAU732#001"
          PreferredNameDe = "Herstellerproduktstammbezeichnung"
          PreferredNameEn = "Manufacturer product root"
          ShortName = "ManProRoo"
          DefinitionDe = "Oberste Ebene einer dreistufigen Produkthierarchie"
          DefinitionEn = "Top level of a three-level product hierarchy" }

        { Id = "0173-1#02-AAU731#001"
          PreferredNameDe = "Herstellerproduktfamilie"
          PreferredNameEn = "Manufacturer product family"
          ShortName = "ManProFam"
          DefinitionDe = "Zweite Ebene einer dreistufigen Produkthierarchie"
          DefinitionEn = "Second level of a three-level product hierarchy" }

        { Id = "0173-1#02-AAO057#002"
          PreferredNameDe = "Herstellerprodukttyp"
          PreferredNameEn = "Manufacturer product type"
          ShortName = "ManProTyp"
          DefinitionDe = "Dritte Ebene einer dreistufigen Produkthierarchie"
          DefinitionEn = "Third level of a three-level product hierarchy" }

        { Id = "0173-1#02-AAO676#003"
          PreferredNameDe = "Produktartikelnummer des Herstellers"
          PreferredNameEn = "Product article number of manufacturer"
          ShortName = "ProArtNumMan"
          DefinitionDe = "Eindeutige Produktkennung des Herstellers"
          DefinitionEn = "Unique product identifier of the manufacturer" }

        { Id = "0173-1#02-AAM556#002"
          PreferredNameDe = "Seriennummer"
          PreferredNameEn = "Serial number"
          ShortName = "SerNum"
          DefinitionDe = "Eindeutige Identifikation eines Produkts"
          DefinitionEn = "Unique identification of a product" }

        { Id = "0173-1#02-AAP906#001"
          PreferredNameDe = "Herstelljahr"
          PreferredNameEn = "Year of construction"
          ShortName = "YeaCon"
          DefinitionDe = "Jahr, in dem das Produkt hergestellt wurde"
          DefinitionEn = "Year in which the product was manufactured" }

        { Id = "0173-1#02-AAR972#002"
          PreferredNameDe = "Herstelldatum"
          PreferredNameEn = "Date of manufacture"
          ShortName = "DatMan"
          DefinitionDe = "Datum, an dem das Produkt hergestellt wurde"
          DefinitionEn = "Date on which the product was manufactured" }

        { Id = "0173-1#02-AAN270#002"
          PreferredNameDe = "Hardwareversion"
          PreferredNameEn = "Hardware version"
          ShortName = "HWVer"
          DefinitionDe = "Version der Hardware des Produkts"
          DefinitionEn = "Hardware version of the product" }

        { Id = "0173-1#02-AAM985#002"
          PreferredNameDe = "Firmwareversion"
          PreferredNameEn = "Firmware version"
          ShortName = "FWVer"
          DefinitionDe = "Version der Firmware des Produkts"
          DefinitionEn = "Firmware version of the product" }

        { Id = "0173-1#02-AAM737#002"
          PreferredNameDe = "Softwareversion"
          PreferredNameEn = "Software version"
          ShortName = "SWVer"
          DefinitionDe = "Version der Software des Produkts"
          DefinitionEn = "Software version of the product" }

        { Id = "0173-1#02-AAO259#003"
          PreferredNameDe = "Ursprungsland"
          PreferredNameEn = "Country of origin"
          ShortName = "CouOri"
          DefinitionDe = "Land, in dem das Produkt hergestellt wurde"
          DefinitionEn = "Country in which the product was manufactured" }

        { Id = "0173-1#02-AAW515#001"
          PreferredNameDe = "Firmenlogo"
          PreferredNameEn = "Company logo"
          ShortName = "ComLog"
          DefinitionDe = "Grafische Darstellung des Firmenlogos"
          DefinitionEn = "Graphic representation of the company logo" }

        // === Address Information ===
        { Id = "0173-1#02-AAO128#002"
          PreferredNameDe = "Straße"
          PreferredNameEn = "Street"
          ShortName = "Str"
          DefinitionDe = "Straße der Firmenadresse"
          DefinitionEn = "Street of the company address" }

        { Id = "0173-1#02-AAO129#002"
          PreferredNameDe = "Postleitzahl"
          PreferredNameEn = "Zip code"
          ShortName = "Zip"
          DefinitionDe = "Postleitzahl der Firmenadresse"
          DefinitionEn = "Zip code of the company address" }

        { Id = "0173-1#02-AAO132#002"
          PreferredNameDe = "Stadt"
          PreferredNameEn = "City/Town"
          ShortName = "City"
          DefinitionDe = "Stadt der Firmenadresse"
          DefinitionEn = "City of the company address" }

        { Id = "0173-1#02-AAO134#002"
          PreferredNameDe = "Ländercode"
          PreferredNameEn = "National code"
          ShortName = "NatCod"
          DefinitionDe = "Ländercode gemäß ISO 3166-1"
          DefinitionEn = "Country code according to ISO 3166-1" }

        // === Phone ===
        { Id = "0173-1#02-AAO136#002"
          PreferredNameDe = "Telefonnummer"
          PreferredNameEn = "Telephone number"
          ShortName = "TelNum"
          DefinitionDe = "Telefonnummer des Kontakts"
          DefinitionEn = "Telephone number of the contact" }

        { Id = "0173-1#02-AAO137#003"
          PreferredNameDe = "Telefontyp"
          PreferredNameEn = "Type of telephone"
          ShortName = "TypTel"
          DefinitionDe = "Art des Telefons"
          DefinitionEn = "Type of telephone" }

        // === Fax ===
        { Id = "0173-1#02-AAO195#003"
          PreferredNameDe = "Faxnummer"
          PreferredNameEn = "Fax number"
          ShortName = "FaxNum"
          DefinitionDe = "Faxnummer des Kontakts"
          DefinitionEn = "Fax number of the contact" }

        { Id = "0173-1#02-AAO196#003"
          PreferredNameDe = "Faxtyp"
          PreferredNameEn = "Type of fax number"
          ShortName = "TypFax"
          DefinitionDe = "Art der Faxnummer"
          DefinitionEn = "Type of fax number" }

        // === Email ===
        { Id = "0173-1#02-AAO198#002"
          PreferredNameDe = "E-Mail-Adresse"
          PreferredNameEn = "Email address"
          ShortName = "Email"
          DefinitionDe = "E-Mail-Adresse des Kontakts"
          DefinitionEn = "Email address of the contact" }

        { Id = "0173-1#02-AAO200#002"
          PreferredNameDe = "Öffentlicher Schlüssel"
          PreferredNameEn = "Public key"
          ShortName = "PubKey"
          DefinitionDe = "Öffentlicher Schlüssel für sichere Kommunikation"
          DefinitionEn = "Public key for secure communication" }

        { Id = "0173-1#02-AAO199#003"
          PreferredNameDe = "E-Mail-Typ"
          PreferredNameEn = "Type of email address"
          ShortName = "TypEmail"
          DefinitionDe = "Art der E-Mail-Adresse"
          DefinitionEn = "Type of email address" }

        // === Markings ===
        { Id = "0173-1#01-AGZ673#001"
          PreferredNameDe = "Kennzeichnungen"
          PreferredNameEn = "Markings"
          ShortName = "Mark"
          DefinitionDe = "Sammlung von Produktkennzeichnungen"
          DefinitionEn = "Collection of product markings" }

        { Id = "0173-1#01-AHD206#001"
          PreferredNameDe = "Kennzeichnung"
          PreferredNameEn = "Marking"
          ShortName = "Mark"
          DefinitionDe = "Einzelne Produktkennzeichnung"
          DefinitionEn = "Single product marking" }

        { Id = "0173-1#02-BAB392#015"
          PreferredNameDe = "Kennzeichnungsname"
          PreferredNameEn = "Marking name"
          ShortName = "MarkNam"
          DefinitionDe = "Name der Kennzeichnung"
          DefinitionEn = "Name of the marking" }

        { Id = "0173-1#02-ABH783#001"
          PreferredNameDe = "Bezeichnung des Zertifikats oder der Genehmigung"
          PreferredNameEn = "Designation of certificate or approval"
          ShortName = "DesCerApp"
          DefinitionDe = "Bezeichnung des zugehörigen Zertifikats oder der Genehmigung"
          DefinitionEn = "Designation of the associated certificate or approval" }

        { Id = "0173-1#02-AAO003#003"
          PreferredNameDe = "Ausstellungsdatum"
          PreferredNameEn = "Issue date"
          ShortName = "IssDat"
          DefinitionDe = "Datum der Ausstellung"
          DefinitionEn = "Date of issue" }

        { Id = "0173-1#02-AAO004#003"
          PreferredNameDe = "Ablaufdatum"
          PreferredNameEn = "Expiry date"
          ShortName = "ExpDat"
          DefinitionDe = "Datum des Ablaufs"
          DefinitionEn = "Date of expiry" }

        { Id = "0173-1#02-AAA801#004"
          PreferredNameDe = "Kennzeichnungsdatei"
          PreferredNameEn = "Marking file"
          ShortName = "MarkFil"
          DefinitionDe = "Datei mit der Kennzeichnung"
          DefinitionEn = "File containing the marking" }

        { Id = "0173-1#02-AAM954#002"
          PreferredNameDe = "Zusätzlicher Kennzeichnungstext"
          PreferredNameEn = "Marking additional text"
          ShortName = "MarkAddTxt"
          DefinitionDe = "Zusätzlicher beschreibender Text zur Kennzeichnung"
          DefinitionEn = "Additional descriptive text for the marking" }

        // === Handover Documentation (IDTA 02004-1-2) ===
        { Id = "0173-1#01-AHF578#001"
          PreferredNameDe = "Übergabedokumentation"
          PreferredNameEn = "Handover documentation"
          ShortName = "HandDoc"
          DefinitionDe = "Dokumentation für die Übergabe"
          DefinitionEn = "Documentation for handover" }

        { Id = "0173-1#02-ABI500#001/0173-1#01-AHF579#001"
          PreferredNameDe = "Dokument"
          PreferredNameEn = "Document"
          ShortName = "Doc"
          DefinitionDe = "Ein einzelnes Dokument"
          DefinitionEn = "A single document" }

        { Id = "0173-1#02-ABI501#001/0173-1#01-AHF580#001"
          PreferredNameDe = "Dokumenten-ID"
          PreferredNameEn = "Document ID"
          ShortName = "DocId"
          DefinitionDe = "Eindeutige Kennung des Dokuments"
          DefinitionEn = "Unique identifier of the document" }

        { Id = "0173-1#02-ABI502#001/0173-1#01-AHF581#001"
          PreferredNameDe = "Dokumentenklassifikation"
          PreferredNameEn = "Document classification"
          ShortName = "DocCla"
          DefinitionDe = "Klassifikation des Dokuments"
          DefinitionEn = "Classification of the document" }

        { Id = "0173-1#02-ABI503#001/0173-1#01-AHF582#001"
          PreferredNameDe = "Dokumentenversion"
          PreferredNameEn = "Document version"
          ShortName = "DocVer"
          DefinitionDe = "Version des Dokuments"
          DefinitionEn = "Version of the document" }

        { Id = "0173-1#02-ABI504#001/0173-1#01-AHF583#001"
          PreferredNameDe = "Digitale Datei"
          PreferredNameEn = "Digital file"
          ShortName = "DigFil"
          DefinitionDe = "Digitale Darstellung des Dokuments"
          DefinitionEn = "Digital representation of the document" }
    ]
