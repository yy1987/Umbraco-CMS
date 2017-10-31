﻿using NPoco;
using Umbraco.Core.Persistence.DatabaseAnnotations;

namespace Umbraco.Core.Models.Rdbms
{
    [TableName(Constants.DatabaseSchema.Tables.Lock)]
    [PrimaryKey("id")]
    [ExplicitColumns]
    internal class LockDto
    {
        [Column("id")]
        [PrimaryKeyColumn(Name = "PK_umbracoLock")]
        public int Id { get; set; }

        [Column("value")]
        [NullSetting(NullSetting = NullSettings.NotNull)]
        public int Value { get; set; } = 1;

        [Column("name")]
        [NullSetting(NullSetting = NullSettings.NotNull)]
        [Length(64)]
        public string Name { get; set; }
    }
}
