[Fact]
public async Task DeleteAsync_RemovesCalendarAndEvents_CascadeDelete()
{
    // Arrange
    var repo = new CalendarRepository();
    var calendar = new Calendar { Id = Guid.NewGuid(), Name = "Test Calendar" };
    await repo.AddAsync(calendar);

    var event1 = new CalendarEvent { Id = Guid.NewGuid(), CalendarId = calendar.Id, Title = "Event 1" };
    var event2 = new CalendarEvent { Id = Guid.NewGuid(), CalendarId = calendar.Id, Title = "Event 2" };
    await repo.AddEventAsync(event1);
    await repo.AddEventAsync(event2);

    // Act
    await repo.DeleteAsync(calendar.Id);

    // Assert
    var deletedCalendar = await repo.GetByIdAsync(calendar.Id);
    Assert.Null(deletedCalendar);

    var events = await repo.GetEventsByCalendarIdAsync(calendar.Id);
    Assert.Empty(events);
}